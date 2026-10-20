// Flagon management console. Plain ES modules, no framework; talks only to the management API (./api.js).
import { api, ApiError } from "./api.js";
import { keyProblem, nearDuplicates } from "./keys.js";

const view = document.getElementById("view");
const notice = document.getElementById("notice");
const STALE_DAYS_KEY = "flagon.staleDays";
const DAY_MS = 24 * 60 * 60 * 1000;

// ---------------------------------------------------------------- helpers

/** Builds an element. Text children are inserted as text, never as HTML. */
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs ?? {})) {
    if (value === undefined || value === null || value === false) continue;
    if (name.startsWith("on")) el.addEventListener(name.slice(2), value);
    else if (name === "class") el.className = value;
    else if (name in el && typeof value !== "string") el[name] = value;
    else el.setAttribute(name, value === true ? "" : value);
  }
  for (const child of children.flat(Infinity)) {
    if (child === undefined || child === null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

const flagHref = (key) => `#/flags/${encodeURIComponent(key)}`;
const segmentHref = (key) => `#/segments/${encodeURIComponent(key)}`;

/** Links to each of `keys` (made with `href`), comma-separated. */
const links = (keys, href) => keys.flatMap((k, i) => [i ? ", " : "", h("a", { href: href(k) }, k)]);

const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

let segmentsSupport = null;

/**
 * Resolves to true unless the management API answers 501 for segments (the store does not support them); asked once.
 * Segment pages, the Segments link and the targeting card's segment controls are hidden without them.
 */
function segmentsSupported() {
  segmentsSupport ??= api.listSegments().then(() => true, (e) => {
    if (e instanceof ApiError && e.status === 501) return false;
    segmentsSupport = null; // a transient failure: ask again next time
    return true;
  });
  return segmentsSupport;
}

/** The segments to offer on a flag page, or null when the store has none (or they could not be loaded). */
async function segmentsForFlags() {
  if (!await segmentsSupported()) return null;
  try { return await api.listSegments(); } catch { return null; }
}

function formatTime(iso) {
  if (!iso) return "never";
  const date = new Date(iso);
  const seconds = Math.round((Date.now() - date.getTime()) / 1000);
  const units = [["year", 31536000], ["month", 2592000], ["day", 86400], ["hour", 3600], ["minute", 60]];
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) {
      const n = Math.round(seconds / size);
      return n > 0 ? `${n} ${unit}${n === 1 ? "" : "s"} ago` : `in ${-n} ${unit}${n === -1 ? "" : "s"}`;
    }
  }
  return "just now";
}

const time = (iso) => h("time", { datetime: iso ?? undefined, title: iso ? new Date(iso).toLocaleString() : undefined }, formatTime(iso));

function showNotice(message, kind = "error") {
  notice.className = `notice ${kind}`;
  notice.textContent = message;
  notice.hidden = !message;
  if (kind === "success") setTimeout(() => { if (notice.textContent === message) notice.hidden = true; }, 4000);
}

function staleDays() {
  try { return Number(localStorage.getItem(STALE_DAYS_KEY)) || 30; } catch { return 30; }
}

function isStale(flag) {
  const last = flag.usage?.lastEvaluatedAt;
  return !last || Date.now() - new Date(last).getTime() > staleDays() * DAY_MS;
}

function parseTags(text) {
  return [...new Set(text.split(",").map((t) => t.trim()).filter(Boolean))];
}

/** True for a boolean flag, the only type with the on/off toggle and percentage rollout slider. */
const isBoolean = (flag) => flag.type === "boolean";

/** A variant value as text: JSON (strings quoted), pretty-printed over several lines for objects when `pretty`. */
const formatValue = (value, pretty = false) => JSON.stringify(value, null, pretty && typeof value === "object" ? 2 : undefined);

const truncate = (text, max = 40) => (text.length > max ? `${text.slice(0, max - 1)}…` : text);

/**
 * A value as typed JSON (strings quoted, objects pretty-printed), or `placeholder` (muted) when there is none. The default
 * placeholder suits a served value: no variant is served, so each app gets its code default.
 */
function valueView(value, placeholder = "none (each app gets its code default)") {
  if (value === null || value === undefined) return h("span", { class: "muted" }, placeholder);
  return typeof value === "object" ? h("pre", { class: "value" }, formatValue(value, true)) : h("code", {}, formatValue(value));
}

/** The list's value cell for a non-boolean flag: its default variant and (truncated) value, or that it has none. */
function valueSummary(flag) {
  if (flag.defaultVariant === null || flag.defaultVariant === undefined) {
    return h("span", { class: "muted small", title: "No default variant: each app gets its code default unless a rule matches" }, "no default");
  }
  const text = formatValue(flag.value);
  return h("span", { class: "value-summary", title: text },
    h("span", { class: "variant" }, flag.defaultVariant), " ", h("code", {}, truncate(text)));
}

function badges(flag) {
  return h("span", { class: "badges" },
    flag.type && h("span", { class: `badge type type-${flag.type}`, title: `A ${flag.type} flag` }, flag.type),
    flag.archived && h("span", { class: "badge archived", title: "Archived: evaluates to the code default" }, "archived"),
    flag.state === "disabled" && h("span", { class: "badge warn", title: "Disabled: each app gets its code default" }, "disabled"),
    flag.source === "auto" && h("span", { class: "badge auto", title: "Created automatically on first evaluation" }, "auto"),
    flag.source === "code" && h("span", { class: "badge code", title: "Defined in code" }, "code"),
    !flag.archived && isStale(flag) && h("span", { class: "badge stale", title: `Not evaluated in the last ${staleDays()} days` }, "stale"),
    flag.usage?.hasDefaultMismatch && h("span", { class: "badge mismatch", title: "Applications pass different code defaults" }, "default mismatch"),
    targetingBadge(flag),
  );
}

/**
 * "rollout N%" for a boolean percentage rollout, "split 34/33/33" for any other split, "segment" for a rule that references
 * segments, "targeted" for any other rule, nothing without one.
 */
function targetingBadge(flag) {
  if (flag.rollout?.percentage !== undefined) return h("span", { class: "badge targeting", title: "Percentage rollout: this share of users gets on" }, `rollout ${flag.rollout.percentage}%`);
  if (flag.rollout) {
    const title = `Percentage split: ${flag.rollout.buckets.map((b) => `${b.variant} ${b.percentage}%`).join(", ")}`;
    return h("span", { class: "badge targeting", title }, `split ${splitWeights(flag.rollout.buckets)}`);
  }
  if (flag.segments?.length) {
    return h("span", { class: "badge targeting", title: `Targets ${flag.segments.length === 1 ? "segment" : "segments"} ${flag.segments.join(", ")}` }, "segment");
  }
  if (hasTargeting(flag)) return h("span", { class: "badge targeting", title: "Has a targeting rule" }, "targeted");
  return null;
}

function toggle(flag, onChange) {
  const input = h("input", {
    type: "checkbox",
    role: "switch",
    checked: flag.enabled,
    disabled: flag.archived,
    "aria-label": `${flag.enabled ? "Disable" : "Enable"} ${flag.key}`,
    onchange: async (e) => {
      e.target.disabled = true;
      let applied = false;
      try { applied = Boolean(await onChange(e.target.checked)); } finally {
        if (!applied && e.target.isConnected) e.target.checked = !e.target.checked;
        e.target.disabled = flag.archived;
      }
    },
  });
  return h("label", { class: "switch", title: flag.archived ? ARCHIVED_LOCKED : undefined }, input, h("span", { class: "slider" }));
}

/** Tooltip on controls disabled because the flag is archived. */
const ARCHIVED_LOCKED = "Restore the flag to change it";

/** Runs a write; on a version conflict shows the reload prompt, on other errors a notice. Returns the result or undefined. */
async function write(action, reload) {
  try {
    return await action();
  } catch (e) {
    if (e instanceof ApiError && e.isVersionConflict) {
      await conflictPrompt(e, reload);
    } else {
      showNotice(e.message);
      await reload?.();
    }
    return undefined;
  }
}

/**
 * Runs a save from a form whose input must survive a failure, disabling `button` meanwhile. Not write(): that reloads
 * and would lose the input. Errors go to `showError` (as ruleError content); only a conflict prompts. True on success.
 */
async function saveInline(action, { button, showError, reload }) {
  if (button) button.disabled = true;
  try {
    await action();
    return true;
  } catch (err) {
    if (err instanceof ApiError && err.isVersionConflict) await conflictPrompt(err, reload);
    else showError(ruleError(err));
    if (button?.isConnected) button.disabled = false;
    return false;
  }
}

/** Asks with `dialog` (confirmDialog options), then clears the flag's targeting and shows `notice`. */
async function confirmClearTargeting(flag, reload, dialog, notice) {
  const ok = await confirmDialog(dialog);
  if (ok && await write(() => api.clearTargeting(flag.key, flag.version), reload)) {
    showNotice(notice, "success");
    await reload();
  }
}

function conflictPrompt(error, reload) {
  const dialog = document.getElementById("conflict");
  const { expectedVersion, currentVersion, key } = error.problem;
  document.getElementById("conflict-detail").textContent =
    `${key ? `'${key}'` : "This flag"} is now at version ${currentVersion}; you were editing version ${expectedVersion}. Your change was not applied.`;
  return new Promise((resolve) => {
    dialog.addEventListener("close", async () => {
      if (dialog.returnValue === "reload") await reload?.();
      resolve();
    }, { once: true });
    dialog.returnValue = "";
    dialog.showModal();
  });
}

function confirmDialog({ title, detail, ok = "Confirm", typed }) {
  const dialog = document.getElementById("confirm");
  const input = document.getElementById("confirm-typed");
  const okButton = document.getElementById("confirm-ok");
  document.getElementById("confirm-title").textContent = title;
  document.getElementById("confirm-detail").textContent = detail;
  document.getElementById("confirm-typed-label").hidden = !typed;
  document.getElementById("confirm-expected").textContent = typed ?? "";
  okButton.textContent = ok;
  input.value = "";
  okButton.disabled = Boolean(typed);
  input.oninput = () => { okButton.disabled = input.value !== typed; };
  return new Promise((resolve) => {
    dialog.addEventListener("close", () => resolve(dialog.returnValue === "ok"), { once: true });
    dialog.returnValue = "";
    dialog.showModal();
    if (typed) input.focus();
  });
}

function errorView(e, [href, label] = ["#/", "Back to flags"]) {
  const message = e instanceof ApiError && e.status === 404 ? "Not found." : e.message;
  return h("div", { class: "empty" }, h("p", {}, message), h("p", {}, h("a", { href }, label)));
}

// ---------------------------------------------------------------- list

const listState = { q: "", tag: "", type: "", state: "", source: "", archived: "active", hygiene: "", targeting: "", segment: "" };

async function renderList() {
  let flags;
  try {
    flags = await api.listFlags();
  } catch (e) {
    return [h("h1", {}, "Flags"), errorView(e)];
  }

  const tags = [...new Set(flags.flatMap((f) => f.tags))].sort();
  const usedSegments = [...new Set([...flags.flatMap((f) => f.segments ?? []), listState.segment].filter(Boolean))].sort();
  const tbody = h("tbody");
  const count = h("span", { class: "muted" });

  const option = (value, label, current) => h("option", { value, selected: value === current }, label);
  const select = (name, label, options) => h("label", {}, label,
    h("select", { onchange: (e) => { listState[name] = e.target.value; draw(); } },
      options.map(([value, text]) => option(value, text, listState[name]))));

  function matches(f) {
    const q = listState.q.trim().toLowerCase();
    if (q && !f.key.toLowerCase().includes(q) && !(f.description ?? "").toLowerCase().includes(q)) return false;
    if (listState.tag && !f.tags.includes(listState.tag)) return false;
    if (listState.type && f.type !== listState.type) return false;
    // On/off is the boolean toggle: non-boolean flags (enabled null) match neither. Disabled is the state, any type.
    if (listState.state === "on" && f.enabled !== true) return false;
    if (listState.state === "off" && f.enabled !== false) return false;
    if (listState.state === "disabled" && f.state !== "disabled") return false;
    if (listState.source && f.source !== listState.source) return false;
    if (listState.archived === "active" && f.archived) return false;
    if (listState.archived === "archived" && !f.archived) return false;
    if (listState.hygiene === "stale" && (f.archived || !isStale(f))) return false;
    if (listState.hygiene === "mismatch" && !f.usage?.hasDefaultMismatch) return false;
    if (listState.targeting === "targeted" && !hasTargeting(f)) return false;
    if (listState.targeting === "rollout" && !f.rollout) return false;
    if (listState.targeting === "none" && hasTargeting(f)) return false;
    if (listState.segment && !(f.segments ?? []).includes(listState.segment)) return false;
    return true;
  }

  function row(f) {
    const apps = f.usage?.applications ?? [];
    return h("tr", { class: f.archived ? "is-archived" : undefined },
      h("td", {}, isBoolean(f)
        ? toggle(f, (enabled) => write(async () => {
          await api.setEnabled(f.key, f.version, enabled);
          showNotice(`${f.key} ${enabled ? "enabled" : "disabled"}.`, "success");
          await refresh();
          return true;
        }, refresh))
        : valueSummary(f)),
      h("td", {}, h("a", { href: flagHref(f.key), class: "key" }, f.key), " ", badges(f),
        f.description && h("div", { class: "muted small" }, f.description)),
      h("td", {}, f.tags.map((t) => h("span", { class: "tag" }, t))),
      h("td", { class: "small" }, apps.length === 0
        ? h("span", { class: "muted" }, "never")
        : h("ul", { class: "apps" }, apps.map((a) => h("li", {}, h("span", { class: "app" }, a.applicationName), " ", time(a.lastEvaluatedAt))))),
    );
  }

  function draw() {
    const shown = flags.filter(matches);
    tbody.replaceChildren(...(shown.length
      ? shown.map(row)
      : [h("tr", {}, h("td", { colspan: 4, class: "empty" }, flags.length ? "No flags match these filters." : "No flags yet."))]));
    count.textContent = `${shown.length} of ${flags.length} flag${flags.length === 1 ? "" : "s"}`;
  }

  async function refresh() {
    try {
      flags = await api.listFlags();
      draw();
    } catch (e) {
      showNotice(e.message);
    }
  }

  const search = h("input", {
    type: "search", placeholder: "Search keys and descriptions", value: listState.q, "aria-label": "Search",
    oninput: (e) => { listState.q = e.target.value; draw(); },
  });

  const filters = h("div", { class: "filters" },
    search,
    select("tag", "Tag", [["", "Any"], ...tags.map((t) => [t, t])]),
    select("type", "Type", [["", "Any"], ["boolean", "Boolean"], ["string", "String"], ["number", "Number"], ["object", "Object"]]),
    select("state", "State", [["", "Any"], ["on", "On"], ["off", "Off"], ["disabled", "Disabled"]]),
    select("source", "Source", [["", "Any"], ["manual", "Manual"], ["auto", "Auto"], ["code", "Code"]]),
    select("archived", "Show", [["active", "Active"], ["archived", "Archived"], ["all", "All"]]),
    select("hygiene", "Hygiene", [["", "Any"], ["stale", "Stale"], ["mismatch", "Default mismatch"]]),
    select("targeting", "Targeting", [["", "Any"], ["targeted", "Targeted"], ["rollout", "Rollout or split"], ["none", "None"]]),
    usedSegments.length > 0 && select("segment", "Uses segment", [["", "Any"], ...usedSegments.map((k) => [k, k])]),
    h("label", {}, "Stale after",
      h("select", {
        onchange: (e) => { try { localStorage.setItem(STALE_DAYS_KEY, e.target.value); } catch { /* ignore */ } draw(); },
      }, [7, 30, 90, 180].map((d) => h("option", { value: d, selected: d === staleDays() }, `${d} days`)))),
  );

  draw();
  setTimeout(() => search.focus());
  return [
    h("div", { class: "title-row" }, h("h1", {}, "Flags"), count),
    filters,
    h("table", { class: "flags" },
      h("thead", {}, h("tr", {}, h("th", {}, "Value"), h("th", {}, "Flag"), h("th", {}, "Tags"), h("th", {}, "Last evaluated"))),
      tbody),
  ];
}

// ---------------------------------------------------------------- targeting

/** True when the flag has a targeting rule; absent, null and `{}` mean none, as on the server. */
function hasTargeting(flag) {
  const t = flag.targeting;
  return t !== null && t !== undefined && !(typeof t === "object" && !Array.isArray(t) && Object.keys(t).length === 0);
}

/** Parses editor text into a rule: empty text means no rule (null). Throws SyntaxError for invalid JSON. */
function parseRule(text) {
  return text.trim() === "" ? null : JSON.parse(text);
}

/**
 * The flagd document the server evaluates for an unsaved rule: the stored flag's state, variants and default variant.
 * Used by Validate and by draft evaluation elsewhere on the detail view.
 */
function draftDocument(flag, rule) {
  const document = { state: flag.state === "enabled" ? "ENABLED" : "DISABLED", variants: flag.variants, defaultVariant: flag.defaultVariant };
  if (rule !== null) document.targeting = rule;
  return document;
}

/** Inline content for an editor error: invalid JSON, a rejected rule (with its location) or anything else. */
function ruleError(e) {
  if (e instanceof SyntaxError) return [h("strong", {}, "Invalid JSON: "), e.message];
  const { path, operator, title } = e instanceof ApiError ? e.problem : {};
  return [
    title && title !== e.message && h("strong", {}, `${title}: `),
    e.message,
    typeof path === "string" && h("span", { class: "small" }, " (at ", h("code", {}, path === "" ? "/" : path),
      operator ? [", operator ", h("code", {}, operator)] : null, ")"),
  ];
}

/**
 * The Targeting card. `segments` are the segments to offer (null when the store has none). Returns the card and
 * `draft`, the editor's unsaved state, for other cards on the detail view:
 * `draft.editing`, `draft.dirty`, `draft.text`, `draft.document()` (the flagd document to evaluate; throws SyntaxError
 * for invalid JSON) and `draft.subscribe(fn)` (called whenever any of those change; returns an unsubscribe function).
 */
function targetingCard(flag, reload, segments) {
  const saved = hasTargeting(flag) ? JSON.stringify(flag.targeting, null, 2) : "";
  const card = h("section", { class: "card stack targeting" });
  const listeners = new Set();
  let editor = null; // the textarea while editing, else null

  const draft = {
    get editing() { return editor !== null; },
    get text() { return editor ? editor.value : saved; },
    get dirty() { return editor !== null && editor.value !== saved; },
    document: () => draftDocument(flag, parseRule(draft.text)),
    subscribe(fn) { listeners.add(fn); return () => listeners.delete(fn); },
  };
  const changed = () => { for (const fn of listeners) fn(draft); };

  const locked = flag.archived ? ARCHIVED_LOCKED : undefined;

  function show() {
    editor = null;
    // Only the server's recognisers (flag.rollout, flag.segmentTargeting) decide what is a split or a segment rule; any
    // other rule is never rewritten by the slider, the split editor or the segment picker.
    const segmentShaped = Boolean(flag.segmentTargeting) && segments !== null;
    const custom = hasTargeting(flag) && !flag.rollout && !segmentShaped;
    // The on/off slider is the boolean two-bucket special case; any other split (or a non-boolean flag) gets the split editor.
    const slider = isBoolean(flag) && (!flag.rollout || flag.rollout?.percentage !== undefined);
    const splittable = Object.keys(flag.variants ?? {}).length >= 2;
    // replaceChildren doesn't flatten like h() does, so flatten and drop the conditional falsy children here.
    card.replaceChildren(...[
      h("h2", {}, "Targeting"),
      flag.segments?.length > 0 && h("p", { class: "small" }, flag.segments.length === 1 ? "Uses segment " : "Uses segments ",
        links(flag.segments, segmentHref), "."),
      custom
        ? [h("p", { class: "small muted" }, "Custom rule: edit as JSON."), h("pre", { class: "rule" }, saved)]
        : segmentShaped
          ? segmentControl()
          : [!flag.rollout && h("p", { class: "muted" }, "No targeting: everyone gets the default variant."),
            slider ? rolloutControl() : splittable && splitControl(),
            !flag.rollout && segments !== null && segmentControl()],
      h("div", { class: "actions" },
        h("button", { disabled: flag.archived, title: locked, onclick: () => edit(saved) }, hasTargeting(flag) ? "Edit JSON" : "Add rule"),
        custom && h("button", { class: "danger", disabled: flag.archived, title: locked, onclick: clear }, "Clear…")),
    ].flat().filter(Boolean));
    changed();
  }

  function edit(text) {
    const message = h("p", { role: "status", hidden: true });
    const status = (content, kind) => {
      message.className = `small ${kind === "ok" ? "ok-text" : "error-text"}`;
      message.replaceChildren(...(content ?? []));
      message.hidden = !content;
    };
    editor = h("textarea", {
      class: "code", rows: 12, spellcheck: "false", autocomplete: "off", "aria-label": "Targeting rule (JSON Logic)",
      placeholder: '{"if": [{"in": [{"var": "email"}, ["a@example.com"]]}, "on", "off"]}',
      oninput: () => { status(); changed(); },
    }, text);

    /** The parsed draft as `{ rule }`, or undefined after showing why it doesn't parse. */
    function parsed() {
      try { return { rule: parseRule(editor.value) }; } catch (e) { status(ruleError(e)); return undefined; }
    }

    /** Inserts {"$ref": key} at the cursor, so a custom rule can use a segment. */
    function insertReference(e) {
      const key = e.target.value;
      if (!key) return;
      const text = JSON.stringify({ $ref: key });
      const { selectionStart: start, selectionEnd: end, value } = editor;
      editor.value = value.slice(0, start) + text + value.slice(end);
      editor.selectionStart = editor.selectionEnd = start + text.length;
      e.target.value = "";
      editor.focus();
      status();
      changed();
    }

    function format() {
      const p = parsed();
      if (!p) return;
      editor.value = p.rule === null ? "" : JSON.stringify(p.rule, null, 2);
      status();
      changed();
    }

    async function validate(e) {
      const p = parsed();
      if (!p) return;
      const button = e.target;
      const submitted = { editor, text: editor.value };
      // The editor stays editable meanwhile: only report on text that is still what was validated.
      const current = () => editor === submitted.editor && editor.value === submitted.text;
      button.disabled = true;
      try {
        const result = await api.evaluate(flag.key, {}, draftDocument(flag, p.rule));
        const served = result.variant ?? "no variant (each app gets its code default)";
        if (current()) status(["Valid rule. With an empty context it returns ", h("code", {}, served), ` (${result.reason}).`], "ok");
      } catch (err) {
        if (current()) status(ruleError(err));
      } finally {
        button.disabled = false;
      }
    }

    async function save(e) {
      e.preventDefault();
      const p = parsed();
      if (!p) return;
      const saved = await saveInline(() => api.setTargeting(flag.key, flag.version, p.rule),
        { button: e.submitter, showError: status, reload });
      if (!saved) return;
      showNotice(p.rule === null ? "Targeting cleared." : "Targeting saved.", "success");
      await reload();
    }

    card.replaceChildren(h("form", { class: "stack", onsubmit: save },
      h("h2", {}, "Targeting"),
      h("p", { class: "small muted" }, "A JSON Logic rule returning a variant name (",
        Object.keys(flag.variants ?? {}).flatMap((name, i, all) => [i ? (i === all.length - 1 ? " or " : ", ") : "", h("code", {}, name)]),
        "); when it matches nothing, the default variant applies. Leave it empty for no targeting."),
      editor,
      message,
      h("div", { class: "actions" },
        h("button", { type: "submit", class: "primary" }, "Save"),
        h("button", { type: "button", onclick: format }, "Format"),
        h("button", { type: "button", onclick: validate }, "Validate"),
        h("button", { type: "button", onclick: show }, "Cancel"),
        segments?.some((sg) => !sg.archived) && h("select", { "aria-label": "Insert segment reference", onchange: insertReference },
          h("option", { value: "" }, "Insert segment reference…"),
          segments.filter((sg) => !sg.archived).map((sg) => h("option", { value: sg.key }, sg.key))))));
    changed();
    editor.focus();
  }

  /** The percentage rollout form: writes the canonical `fractional` shape the server reports back as `flag.rollout`. */
  function rolloutControl() {
    const current = flag.rollout?.percentage;
    let n = current ?? 0;
    const message = h("p", { role: "status", class: "small error-text", hidden: true });
    const share = h("strong", {});
    const range = h("input", { type: "range", min: "0", max: "100", step: "1", value: n, disabled: flag.archived, title: locked,
      "aria-label": "Rollout percentage", oninput: () => set(range.value) });
    const number = h("input", { type: "number", class: "percent", min: "0", max: "100", step: "1", value: n, disabled: flag.archived, title: locked,
      "aria-label": "Rollout percentage", oninput: () => { if (number.value !== "") set(number.value, true); }, onchange: () => set(number.value) });
    const saveButton = h("button", { type: "submit", class: "primary", disabled: flag.archived, title: locked }, current === undefined ? "Start rollout" : "Save rollout");

    /**
     * Clamps to an integer 0–100 and syncs both inputs (except the number box mid-typing). Empty or invalid input
     * (Number("") would be 0) keeps the previous value, which the number box gets back once typing ends.
     */
    function set(value, typing = false) {
      const parsed = value === "" ? NaN : Math.round(Number(value));
      n = Number.isFinite(parsed) ? Math.min(100, Math.max(0, parsed)) : n;
      range.value = n;
      if (!typing) number.value = n;
      share.textContent = `${n}%`;
      saveButton.disabled = flag.archived || n === current;
      message.hidden = true;
    }

    async function save(e) {
      e.preventDefault();
      const showError = (content) => { message.replaceChildren(...content); message.hidden = false; };
      const saved = await saveInline(() => api.setTargeting(flag.key, flag.version, { fractional: [["on", n], ["off", 100 - n]] }),
        { button: saveButton, showError, reload });
      if (!saved) return;
      showNotice(`Rollout saved: ${n}% of users get on.`, "success");
      await reload();
    }

    const remove = () => confirmClearTargeting(flag, reload, {
      title: `Remove the rollout for ${flag.key}?`,
      detail: "Targeting is cleared and everyone gets the default variant. The audit history keeps a copy.",
      ok: "Remove",
    }, "Rollout removed.");

    set(n);
    return h("form", { class: "stack rollout", onsubmit: save },
      h("h3", {}, "Percentage rollout"),
      h("div", { class: "rollout-inputs" }, range, number, h("span", {}, "%")),
      h("p", {}, share, " of users get ", h("strong", {}, "on"), "; the rest get ", h("code", {}, "off"), "."),
      h("p", { class: "small muted" }, "Users are bucketed by their targeting key, so the same user stays in the same bucket as the percentage grows."),
      message,
      h("div", { class: "actions" },
        saveButton,
        current !== undefined && h("button", { type: "button", class: "danger", disabled: flag.archived, title: locked, onclick: remove }, "Remove rollout…")));
  }

  /**
   * The percentage split form: one weight per variant (the current split's buckets first, in rule order, so saving
   * keeps users in their buckets), summing to 100. Writes the canonical `fractional` shape the server reports back as
   * `flag.rollout`, with a bucket for every variant (0% ones included).
   */
  function splitControl() {
    const current = flag.rollout?.buckets ?? [];
    const names = [...current.map((b) => b.variant), ...Object.keys(flag.variants).filter((v) => !current.some((b) => b.variant === v))];
    const initial = names.map((v) => current.find((b) => b.variant === v)?.percentage ?? 0);
    const message = h("p", { role: "status", class: "small error-text", hidden: true });
    const sum = h("p", { class: "small", role: "status" });
    const saveButton = h("button", { type: "submit", class: "primary", disabled: true, title: locked }, current.length ? "Save split" : "Start split");
    const inputs = names.map((name, i) => h("input", {
      type: "number", class: "percent", min: "0", max: "100", step: "1", value: initial[i], disabled: flag.archived, title: locked,
      "aria-label": `Percentage of users who get ${name}`, oninput: update,
    }));

    /** The weights as entered (NaN for an empty or non-integral box). */
    const weights = () => inputs.map((input) => input.value === "" ? NaN : Number(input.value));

    function update() {
      const w = weights();
      const valid = w.every((n) => Number.isInteger(n) && n >= 0 && n <= 100);
      const total = valid ? w.reduce((a, n) => a + n, 0) : NaN;
      sum.textContent = valid ? `Total: ${total}%${total === 100 ? "" : " (the weights must add up to 100%)"}` : "Each weight must be a whole number from 0 to 100.";
      sum.className = `small ${total === 100 ? "muted" : "error-text"}`;
      saveButton.disabled = flag.archived || total !== 100 || w.every((n, i) => n === initial[i]);
      message.hidden = true;
    }

    /** 100 shared as evenly as whole numbers allow, the remainder going to the first variants (34/33/33). */
    function even() {
      const share = Math.floor(100 / inputs.length), extra = 100 % inputs.length;
      inputs.forEach((input, i) => { input.value = share + (i < extra ? 1 : 0); });
      update();
    }

    async function save(e) {
      e.preventDefault();
      const w = weights();
      // Every weight as entered, 0% included: a zero-width bucket serves nobody, but keeping it means the split reads
      // back as the admin left it and its variant stays guarded against removal.
      const buckets = names.map((variant, i) => ({ variant, percentage: w[i] }));
      const showError = (content) => { message.replaceChildren(...content); message.hidden = false; };
      const saved = await saveInline(() => api.setTargeting(flag.key, flag.version, { fractional: buckets.map((b) => [b.variant, b.percentage]) }),
        { button: saveButton, showError, reload });
      if (!saved) return;
      showNotice(`Split saved: ${buckets.map((b) => `${b.variant} ${b.percentage}%`).join(", ")}.`, "success");
      await reload();
    }

    const remove = () => confirmClearTargeting(flag, reload, {
      title: `Remove the split for ${flag.key}?`,
      detail: "Targeting is cleared and everyone gets the default variant. The audit history keeps a copy.",
      ok: "Remove",
    }, "Split removed.");

    update();
    return h("form", { class: "stack rollout", onsubmit: save },
      h("h3", {}, "Percentage split"),
      h("div", { class: "split-weights" },
        names.map((name, i) => h("label", {}, h("code", {}, name), h("span", { class: "rollout-inputs" }, inputs[i], h("span", {}, "%"))))),
      sum,
      h("p", { class: "small muted" }, "Users are bucketed by their targeting key, so each user consistently gets the same variant."),
      message,
      h("div", { class: "actions" },
        saveButton,
        h("button", { type: "button", disabled: flag.archived, title: locked, onclick: even }, "Even split"),
        current.length > 0 && h("button", { type: "button", class: "danger", disabled: flag.archived, title: locked, onclick: remove }, "Remove split…")));
  }

  /**
   * "Serve a variant to segments": writes the canonical shape the server reports back as `flag.segmentTargeting`
   * ({"if":[{"$ref":"a"},"v",null]}, or {"or":[…]} over several). Archived segments are offered only when already chosen.
   */
  function segmentControl() {
    const current = flag.segmentTargeting;
    const chosen = new Set(current?.segments ?? []);
    const offered = segments.filter((sg) => !sg.archived || chosen.has(sg.key));
    if (!current && offered.length === 0) {
      return h("p", { class: "small muted" }, "No segments yet: ", h("a", { href: "#/segments/new" }, "create one"),
        " to target a reusable audience from any flag.");
    }

    const boxes = offered.map((sg) => ({ key: sg.key, input: h("input", { type: "checkbox", checked: chosen.has(sg.key), disabled: flag.archived }) }));
    const names = Object.keys(flag.variants ?? {});
    const preferred = current?.variant ?? (isBoolean(flag) ? "on" : names[0]);
    const variant = h("select", { disabled: flag.archived, "aria-label": "Variant to serve" },
      names.map((n) => h("option", { value: n, selected: n === preferred }, n)));
    const message = h("p", { role: "status", class: "small error-text", hidden: true });
    const showError = (content) => { message.replaceChildren(h("span", {}, content)); message.hidden = false; };

    async function save(e) {
      e.preventDefault();
      const keys = boxes.filter((b) => b.input.checked).map((b) => b.key);
      if (keys.length === 0) { showError("Pick at least one segment."); return; }
      const refs = keys.map((k) => ({ $ref: k }));
      const rule = { if: [refs.length === 1 ? refs[0] : { or: refs }, variant.value, null] };
      if (await saveInline(() => api.setTargeting(flag.key, flag.version, rule), { button: e.submitter, showError, reload })) {
        showNotice("Segment targeting saved.", "success");
        await reload();
      }
    }

    const remove = () => confirmClearTargeting(flag, reload, {
      title: `Stop targeting segments on ${flag.key}?`,
      detail: "The rule is removed and everyone gets the default variant. The audit history keeps a copy.",
      ok: "Remove",
    }, "Segment targeting removed.");

    return h("form", { class: "stack rollout", onsubmit: save },
      h("h3", {}, "Segments"),
      h("p", { class: "small muted" }, "Serve a variant to anyone in any of the chosen segments; everyone else gets the default variant."),
      h("div", { class: "segment-choices" }, boxes.map((b) => h("label", { class: "inline" }, b.input,
        h("a", { href: segmentHref(b.key) }, b.key),
        segments.find((sg) => sg.key === b.key)?.archived && h("span", { class: "badge archived" }, "archived")))),
      h("label", {}, "Serve", variant),
      message,
      h("div", { class: "actions" },
        h("button", { type: "submit", class: "primary", disabled: flag.archived, title: locked }, current ? "Save segments" : "Target segments"),
        current && h("button", { type: "button", class: "danger", disabled: flag.archived, title: locked, onclick: remove }, "Remove…")));
  }

  const clear = () => confirmClearTargeting(flag, reload, {
    title: `Clear targeting for ${flag.key}?`,
    detail: "The rule is removed and everyone gets the default variant. The audit history keeps a copy.",
    ok: "Clear",
  }, "Targeting cleared.");

  show();
  return { card, draft };
}

// ---------------------------------------------------------------- try it

/** An attribute value as typed: JSON when it parses (numbers, booleans, arrays…), otherwise the text itself. */
function attributeValue(text) {
  try { return JSON.parse(text); } catch { return text; }
}

/**
 * The "Try it" card: evaluates the saved flag, or the Targeting editor's unsaved `draft`, for a context typed here.
 * Read-only by design: it calls nothing but the side-effect-free evaluate endpoint. The last context is remembered per
 * flag in this browser only, as a convenience.
 */
function tryItCard(flag, draft) {
  const storageKey = `flagon.tryit.${flag.key}`;
  let remembered = {};
  try { remembered = JSON.parse(localStorage.getItem(storageKey) ?? "{}") ?? {}; } catch { /* ignore */ }

  const remember = () => {
    const value = { targetingKey: targetingKey.value, attributes: rows.map((r) => [r.key.value, r.value.value]) };
    try { localStorage.setItem(storageKey, JSON.stringify(value)); } catch { /* ignore */ }
    // Results no longer describe the typed context once it changes.
    if ((outcomes.saved || outcomes.draft) && contextKey() !== outcomesContext) {
      outcomes.saved = outcomes.draft = null;
      drawResults();
    }
  };

  const targetingKey = h("input", {
    value: typeof remembered.targetingKey === "string" ? remembered.targetingKey : "",
    placeholder: "e.g. user-123", spellcheck: "false", autocomplete: "off", oninput: remember,
  });
  const rowList = h("div", { class: "stack attributes" });
  const rows = [];

  function addRow(key = "", value = "") {
    const row = {
      key: h("input", { value: key, placeholder: "attribute", "aria-label": "Attribute name", spellcheck: "false", autocomplete: "off", oninput: remember }),
      value: h("input", { value, placeholder: "value (JSON or text)", "aria-label": "Attribute value", spellcheck: "false", autocomplete: "off", oninput: remember }),
    };
    row.el = h("div", { class: "attribute" }, row.key, row.value,
      h("button", { type: "button", title: "Remove attribute", "aria-label": "Remove attribute", onclick: () => {
        rows.splice(rows.indexOf(row), 1);
        row.el.remove();
        remember();
      } }, "×"));
    rows.push(row);
    rowList.append(row.el);
    return row;
  }
  for (const pair of Array.isArray(remembered.attributes) ? remembered.attributes : []) {
    if (Array.isArray(pair)) addRow(String(pair[0] ?? ""), String(pair[1] ?? ""));
  }

  /** The evaluation context: attributes with a name, then the targeting key when one is given. */
  function context() {
    const ctx = {};
    for (const r of rows) {
      const name = r.key.value.trim();
      if (name && name !== "targetingKey") ctx[name] = attributeValue(r.value.value);
    }
    if (targetingKey.value.trim()) ctx.targetingKey = targetingKey.value.trim();
    return ctx;
  }

  const contextKey = () => JSON.stringify(context());

  const results = h("div", { class: "tryit-results" });
  const outcomes = { saved: null, draft: null }; // each a rendered result column, or null when not run (or stale)
  let outcomesContext = null; // contextKey() the shown outcomes were evaluated for

  function drawResults() {
    results.replaceChildren(...[outcomes.saved, outcomes.draft].filter(Boolean));
    results.hidden = !outcomes.saved && !outcomes.draft;
  }

  function resultColumn(title, content) {
    return h("div", { class: "tryit-result" }, h("h3", {}, title), content);
  }

  function describeResult(r) {
    return h("dl", {},
      h("dt", {}, "Value"), h("dd", {}, valueView(r.value)),
      h("dt", {}, "Variant"), h("dd", {}, r.variant != null ? h("code", {}, r.variant) : h("span", { class: "muted" }, "none")),
      h("dt", {}, "Reason"), h("dd", {}, r.reason),
      r.errorCode && [h("dt", {}, "Error"), h("dd", { class: "error-text" }, r.errorCode, r.errorMessage ? `: ${r.errorMessage}` : "")]);
  }

  async function run(which, e) {
    const button = e.currentTarget;
    const ctx = context();
    const key = JSON.stringify(ctx);
    const show = (column) => {
      if (outcomesContext !== key) { outcomes.saved = outcomes.draft = null; outcomesContext = key; }
      outcomes[which] = column;
      drawResults();
    };
    let document;
    if (which === "draft") {
      try { document = draft.document(); } catch (err) {
        // Invalid JSON in the editor: shown here, never as a page notice.
        show(resultColumn("Draft", h("p", { class: "small error-text" }, ruleError(err))));
        return;
      }
    }
    const draftText = draft.text;
    // A response is dropped if the context (or, for the draft, the editor) changed while it was in flight.
    const current = () => contextKey() === key && (which !== "draft" || (draft.dirty && draft.text === draftText));
    button.disabled = true;
    const title = which === "draft" ? "Draft" : "Saved";
    let column;
    try {
      const result = await api.evaluate(flag.key, ctx, document);
      column = resultColumn(title, describeResult(result));
    } catch (err) {
      // Compile errors (with their location) and context errors stay inline in this card.
      column = resultColumn(title, h("p", { class: "small error-text" }, ruleError(err)));
    } finally {
      button.disabled = false;
    }
    if (current()) show(column);
  }

  const draftButton = h("button", { type: "button", onclick: (e) => run("draft", e) }, "Evaluate draft");
  const syncDraft = () => {
    draftButton.hidden = !draft.dirty;
    // A draft result no longer describes the editor once its text changes.
    if (outcomes.draft) { outcomes.draft = null; drawResults(); }
  };
  draft.subscribe(syncDraft);
  draftButton.hidden = !draft.dirty;
  drawResults();

  return h("section", { class: "card stack tryit" },
    h("h2", {}, "Try it"),
    h("p", { class: "small muted" }, "Preview what this flag returns for a user. Nothing is saved and no usage is recorded."),
    h("label", {}, "Targeting key", targetingKey),
    h("div", { class: "stack" }, h("strong", { class: "small" }, "Attributes"), rowList,
      h("div", {}, h("button", { type: "button", onclick: () => { addRow().key.focus(); remember(); } }, "Add attribute"))),
    h("div", { class: "actions" },
      h("button", { type: "button", class: "primary", onclick: (e) => run("saved", e) }, "Evaluate saved"),
      draftButton),
    results);
}

// ---------------------------------------------------------------- detail

async function renderDetail(key) {
  let flag, audit, segments;
  try {
    [flag, audit, segments] = await Promise.all([api.getFlag(key), api.audit(key), segmentsForFlags()]);
  } catch (e) {
    return [h("p", {}, h("a", { href: "#/" }, "← Flags")), errorView(e)];
  }

  const reload = () => route();
  // targeting.draft is the editor's unsaved rule, for other cards on this view to read (see targetingCard).
  const targeting = targetingCard(flag, reload, segments);

  const description = h("textarea", { rows: 2, maxlength: 2000 }, flag.description ?? "");
  const tags = h("input", { value: flag.tags.join(", "), placeholder: "comma, separated, tags", spellcheck: "false" });
  const metadata = h("form", {
    class: "card stack",
    onsubmit: async (e) => {
      e.preventDefault();
      const result = await write(() => api.updateMetadata(flag.key, flag.version, {
        description: description.value.trim(),
        tags: parseTags(tags.value),
      }), reload);
      if (result) { showNotice("Saved.", "success"); await reload(); }
    },
  },
  h("h2", {}, "Details"),
  h("label", {}, "Description", description),
  h("label", {}, "Tags", tags),
  h("div", {}, h("button", { type: "submit", class: "primary" }, "Save")));

  const lifecycle = h("section", { class: "card stack" }, h("h2", {}, "Lifecycle"),
    flag.archived
      ? [
        h("p", {}, "This flag is archived: every application gets its code default. Restore it to manage it again, or purge it to delete it permanently (its audit history is kept)."),
        h("div", { class: "actions" },
          h("button", {
            onclick: async () => {
              if (await write(() => api.restore(flag.key, flag.version), reload)) { showNotice("Restored.", "success"); await reload(); }
            },
          }, "Restore"),
          h("button", {
            class: "danger",
            onclick: async () => {
              const ok = await confirmDialog({
                title: `Purge ${flag.key}?`,
                detail: "The flag and its usage data are deleted permanently. Applications still evaluating it get their code default (and may auto-create it again).",
                ok: "Purge",
                typed: flag.key,
              });
              if (!ok) return;
              const done = await write(async () => { await api.purge(flag.key, flag.version); return true; }, reload);
              if (done) { showNotice(`${flag.key} purged.`, "success"); location.hash = "#/"; }
            },
          }, "Purge…")),
      ]
      : [
        h("p", {}, "Archive a flag that is no longer needed: applications get their code default, and it can be restored later."),
        h("div", { class: "actions" },
          h("button", {
            onclick: async () => {
              const ok = await confirmDialog({
                title: `Archive ${flag.key}?`,
                detail: "Every application evaluating this flag will get its code default until it is restored.",
                ok: "Archive",
              });
              if (ok && await write(() => api.archive(flag.key, flag.version), reload)) { showNotice("Archived.", "success"); await reload(); }
            },
          }, "Archive…")),
      ]);

  const apps = flag.usage?.applications ?? [];
  const usage = h("section", { class: "card" }, h("h2", {}, "Usage"),
    apps.length === 0
      ? h("p", { class: "muted" }, "No application has evaluated this flag yet.")
      : h("table", {},
        h("thead", {}, h("tr", {}, h("th", {}, "Application"), h("th", {}, "Last evaluated"), h("th", {}, "Code default"))),
        h("tbody", {}, apps.map((a) => h("tr", {},
          h("td", {}, a.applicationName),
          h("td", {}, time(a.lastEvaluatedAt)),
          h("td", {}, valueView(a.observedDefault, "—")))))),
    flag.usage?.hasDefaultMismatch && h("p", { class: "warning" }, "Applications pass different code defaults for this flag, so they disagree whenever it is archived, disabled or unavailable."));

  const history = h("section", { class: "card" }, h("h2", {}, "Audit history"),
    audit.length === 0
      ? h("p", { class: "muted" }, "No recorded changes.")
      : h("table", { class: "audit" },
        h("thead", {}, h("tr", {}, h("th", {}, "When"), h("th", {}, "Action"), h("th", {}, "By"), h("th", {}, "Application"), h("th", {}, "Version"), h("th", {}, "Change"))),
        h("tbody", {}, audit.map((entry) => h("tr", {},
          h("td", {}, time(entry.timestamp)),
          h("td", {}, h("span", { class: `badge action-${entry.action}` }, entry.action)),
          h("td", {}, entry.actor ?? h("span", { class: "muted" }, "system")),
          h("td", {}, entry.applicationName ?? ""),
          h("td", {}, String(entry.version)),
          h("td", {}, describeChange(entry), restoreRuleActions(flag, entry, reload)))))));

  return [
    h("p", {}, h("a", { href: "#/" }, "← Flags")),
    h("div", { class: "title-row" },
      isBoolean(flag) && toggle(flag, async (enabled) => {
        if (await write(() => api.setEnabled(flag.key, flag.version, enabled), reload)) {
          showNotice(`${flag.key} ${enabled ? "enabled" : "disabled"}.`, "success");
          await reload();
          return true;
        }
        return false;
      }),
      h("h1", { class: "key" }, flag.key), badges(flag)),
    h("p", { class: "muted small" },
      `Version ${flag.version} · source ${flag.source} · created `, time(flag.createdAt), flag.createdBy ? ` by ${flag.createdBy}` : "",
      " · updated ", time(flag.updatedAt), flag.updatedBy ? ` by ${flag.updatedBy}` : ""),
    h("div", { class: "grid" }, metadata, lifecycle),
    stateCard(flag, reload),
    !isBoolean(flag) && variantsCard(flag, reload),
    targeting.card,
    tryItCard(flag, targeting.draft),
    usage,
    history,
  ];
}

/**
 * The Enabled/Disabled state switch, for a flag of any type. Disabling is a kill switch: each app gets its code
 * default (reason DISABLED); the variants, default variant and targeting are kept for when it is enabled again.
 */
function stateCard(flag, reload) {
  const enabled = flag.state === "enabled";
  const served = isBoolean(flag) ? "its on/off value and targeting" : "its default variant and targeting";
  const confirmation = (enable) => enable
    ? {
      title: `Enable ${flag.key}?`,
      detail: `Applications stop getting their code default and get this flag's value again (${served}).`,
      ok: "Enable",
    }
    : {
      title: `Disable ${flag.key}?`,
      detail: "Every application evaluating this flag will get its own code default (reason DISABLED), whatever its variants and targeting say. You can enable it again at any time.",
      ok: "Disable",
    };
  const stateSwitch = toggle({ ...flag, enabled }, async (enable) => {
    if (!await confirmDialog(confirmation(enable))) return false;
    if (await write(() => api.setState(flag.key, flag.version, enable ? "enabled" : "disabled"), reload)) {
      showNotice(`${flag.key} ${enable ? "enabled" : "disabled"}.`, "success");
      await reload();
      return true;
    }
    return false;
  });
  stateSwitch.querySelector("input").setAttribute("aria-label", `${enabled ? "Disable" : "Enable"} ${flag.key} (state)`);
  return h("section", { class: "card stack state" },
    h("h2", {}, "State"),
    h("div", { class: "title-row" }, stateSwitch, h("strong", {}, enabled ? "Enabled" : "Disabled")),
    h("p", { class: "small muted" }, enabled
      ? `Serving ${served}. Disable it and each app gets its code default.`
      : "Disabled: each app gets its code default. Enable it to serve its value again."));
}

/** "enabled" / "disabled" for a flagd document state (ENABLED / DISABLED). */
function stateName(state) {
  return typeof state === "string" ? state.toLowerCase() : String(state);
}

/**
 * The variants of a non-boolean flag, with the default variant marked and a default-variant picker; "Edit variants"
 * turns it into an editor that adds, removes and changes variants (renaming is a remove plus an add) and saves them
 * with the default variant in one write. The editor warns when the saved targeting rule mentions a removed name.
 */
function variantsCard(flag, reload) {
  const card = h("section", { class: "card stack variants" });
  const locked = flag.archived ? ARCHIVED_LOCKED : undefined;
  const saved = flag.variants ?? {};
  const noDefault = flag.defaultVariant === null || flag.defaultVariant === undefined;

  function show() {
    const names = Object.keys(saved);
    const picker = h("select", { "aria-label": "Default variant", disabled: flag.archived, title: locked },
      ...names.map((n) => h("option", { value: n, selected: n === flag.defaultVariant }, n)),
      h("option", { value: "", selected: noDefault }, "(none: each app gets its code default)"));
    const setDefault = h("button", { type: "submit", disabled: true, title: locked }, "Set default");
    picker.addEventListener("change", () => { setDefault.disabled = flag.archived || (picker.value || null) === (flag.defaultVariant ?? null); });

    async function saveDefault(e) {
      e.preventDefault();
      const variant = picker.value || null;
      if (await write(() => api.setDefaultVariant(flag.key, flag.version, variant), reload)) {
        showNotice(variant === null ? "Default variant cleared." : `Default variant set to ${variant}.`, "success");
        await reload();
      }
    }

    card.replaceChildren(...[
      h("h2", {}, "Variants"),
      h("table", {},
        h("thead", {}, h("tr", {}, h("th", {}, "Name"), h("th", {}, "Value"))),
        h("tbody", {}, names.map((name) => h("tr", {},
          h("td", {}, h("code", {}, name), name === flag.defaultVariant && [" ", h("span", { class: "badge targeting", title: "Served when no targeting rule matches" }, "default")]),
          h("td", {}, valueView(saved[name])))))),
      noDefault && h("p", { class: "small muted" }, "No default variant: when no rule matches, each app gets its code default."),
      h("form", { class: "inline-form", onsubmit: saveDefault }, h("label", { class: "inline" }, "Default variant", picker), setDefault),
      // A flag with mixed or no variants (written by another tool) has no type to edit its values as.
      h("div", { class: "actions" }, h("button", { disabled: flag.archived || !flag.type, title: locked, onclick: edit }, "Edit variants")),
    ].filter(Boolean));
  }

  function edit() {
    const message = h("p", { role: "status", class: "small error-text", hidden: true });
    const warning = h("p", { class: "warning", hidden: true });
    const literals = stringLiterals(flag.targeting);
    const rows = variantRows(() => flag.type, { initialDefault: flag.defaultVariant ?? null, onChange: warn });
    const showError = (content) => { message.replaceChildren(...content); message.hidden = false; };

    /** Warns about removed names the saved rule still mentions: it would serve an unknown variant (an error) for them. */
    function warn() {
      message.hidden = true;
      const kept = new Set(rows.names());
      const stale = Object.keys(saved).filter((name) => !kept.has(name) && literals.has(name));
      warning.hidden = stale.length === 0;
      warning.replaceChildren("The targeting rule mentions ",
        ...stale.flatMap((name, i) => [i ? ", " : "", h("code", {}, name)]),
        `, which ${stale.length === 1 ? "is" : "are"} removed here: wherever the rule returns ${stale.length === 1 ? "it" : "them"}, evaluation fails and each app gets its code default until the rule is updated. Renaming a variant is a remove plus an add.`);
    }

    async function save(e) {
      e.preventDefault();
      let variants;
      try { variants = rows.value(); } catch (err) { showError([err.message]); return; }
      const defaultVariant = rows.defaultVariant();
      const ok = await saveInline(() => api.setVariants(flag.key, flag.version, variants, defaultVariant),
        { button: e.submitter, showError, reload });
      if (!ok) return;
      showNotice("Variants saved.", "success");
      await reload();
    }

    for (const [name, value] of Object.entries(saved)) rows.add(name, value);
    card.replaceChildren(h("form", { class: "stack variants-editor", onsubmit: save },
      h("h2", {}, "Variants"),
      h("p", { class: "small muted" }, `Every value must be a ${flag.type}: a flag's type can't change. Renaming a variant is a remove plus an add.`),
      rows.list,
      h("div", {}, h("button", { type: "button", onclick: () => rows.add().name.focus() }, "Add variant")),
      h("label", {}, "Default variant", rows.picker),
      warning,
      message,
      h("div", { class: "actions" },
        h("button", { type: "submit", class: "primary" }, "Save variants"),
        h("button", { type: "button", onclick: show }, "Cancel"))));
    warn();
  }

  show();
  return card;
}

/** Every string literal in a JSON value (a targeting rule), for spotting variant names it mentions. */
function stringLiterals(value, found = new Set()) {
  if (typeof value === "string") found.add(value);
  else if (Array.isArray(value)) value.forEach((v) => stringLiterals(v, found));
  else if (value && typeof value === "object") Object.values(value).forEach((v) => stringLiterals(v, found));
  return found;
}

/** Summaries of the variants added, removed and changed between two audited documents. */
function describeVariantChanges(before, after) {
  const was = before.variants ?? {}, now = after.variants ?? {};
  const changes = [];
  for (const name of Object.keys(now)) {
    if (!Object.hasOwn(was, name)) changes.push(`variant ${name} added`);
    else if (JSON.stringify(was[name]) !== JSON.stringify(now[name])) {
      changes.push(`variant ${name} ${truncate(formatValue(was[name]), 24)} → ${truncate(formatValue(now[name]), 24)}`);
    }
  }
  for (const name of Object.keys(was)) {
    if (!Object.hasOwn(now, name)) changes.push(`variant ${name} removed`);
  }
  return changes;
}

/** A short human summary of an audit entry, with the raw documents behind a disclosure. */
function describeChange(entry) {
  const before = entry.before, after = entry.after;
  const changes = [];
  if (before && after) {
    changes.push(...describeVariantChanges(before, after));
    if (before.defaultVariant !== after.defaultVariant) changes.push(`default ${before.defaultVariant ?? "none"} → ${after.defaultVariant ?? "none"}`);
    if (before.state !== after.state) changes.push(`state ${stateName(before.state)} → ${stateName(after.state)}`);
    const bm = before.metadata ?? {}, am = after.metadata ?? {};
    if ((bm.description ?? "") !== (am.description ?? "")) changes.push("description");
    if ((bm.tags ?? "") !== (am.tags ?? "")) changes.push(`tags → ${am.tags || "none"}`);
  } else if (after) {
    changes.push(`default ${after.defaultVariant ?? "none"}`);
  }
  // A purge has no "after"; that is not a targeting removal.
  const targetingChange = after && describeTargetingChange(before, after);
  if (targetingChange) changes.push(targetingChange);
  const raw = { before, after };
  return h("details", {},
    h("summary", {}, changes.join(", ") || "details"),
    h("pre", {}, JSON.stringify(raw, null, 2)));
}

/**
 * "Restore this rule" for each side of an audit entry whose rule differs from the flag's current one.
 * A side without a document (before a create, after a purge) has nothing to restore; when both sides carry
 * the same rule only one action is shown.
 */
function restoreRuleActions(flag, entry, reload) {
  const current = ruleOf(flag);
  const sides = [["Before", entry.before], ["After", entry.after]]
    .filter(([, document]) => document)
    .map(([label, document]) => [label, ruleOf(document)])
    .filter(([, rule]) => !sameRule(rule, current));
  if (sides.length === 2 && sameRule(sides[0][1], sides[1][1])) sides.shift();
  if (sides.length === 0) return null;
  return h("div", { class: "restore-rule" },
    sides.map(([label, rule]) => h("span", {}, `${label}: `, restoreRuleButton(flag, entry, label, rule, reload))));
}

/** Puts an audited rule back with the flag's current version (or clears targeting when that side had none). */
function restoreRuleButton(flag, entry, label, rule, reload) {
  const side = `${label.toLowerCase()} the change to version ${entry.version}`;
  async function restore() {
    const buckets = splitBuckets(rule, flag.variants), percentage = onPercentage(buckets, flag.variants);
    const shape = buckets === null ? "" : percentage === null ? ` (split ${splitWeights(buckets)})` : ` (rollout ${percentage}%)`;
    const ok = await confirmDialog({
      title: `Restore this rule on ${flag.key}?`,
      detail: rule === null
        ? `The flag had no targeting ${side}, so the current rule is removed and everyone gets the default variant.`
        : `The current rule is replaced by the one in place ${side}${shape}. The audit history keeps a copy of the current rule.`,
      ok: "Restore",
    });
    if (!ok) return;
    const done = await write(() => rule === null
      ? api.clearTargeting(flag.key, flag.version)
      : api.setTargeting(flag.key, flag.version, rule), reload);
    if (done) {
      showNotice("Targeting rule restored.", "success");
      await reload();
    }
  }
  return h("button", {
    class: "small",
    disabled: flag.archived,
    title: flag.archived ? ARCHIVED_LOCKED : undefined,
    "aria-label": `Restore the rule in place ${side}`,
    onclick: restore,
  }, "Restore this rule");
}

/** The rule a flagd document carries, or null when it has none (absent, null and `{}` alike). */
function ruleOf(document) {
  return document && hasTargeting(document) ? document.targeting : null;
}

/** True when two rules (or nulls) are the same; the server keeps key order, so serialised text compares exactly. */
function sameRule(a, b) {
  return JSON.stringify(a) === JSON.stringify(b);
}

/**
 * The buckets ([{ variant, percentage }], in rule order) of a canonical split, exactly
 * {"fractional": [[v1, w1], …, [vn, wn]]} with n ≥ 2, literal distinct names defined in `variants`, and integral
 * weights from 0 to 100 summing to 100 (JSON 25.0 parses to 25, so it counts, as on the server), else null. Mirrors
 * the server's RolloutShape (which fills `flag.rollout`); audit documents carry no `rollout`, so the history needs
 * this client-side copy. The shapes both must agree on: tests/Flagon.Tests.Support/RolloutShapes.cs.
 */
function splitBuckets(rule, variants) {
  if (!rule || typeof rule !== "object" || Array.isArray(rule)) return null;
  const keys = Object.keys(rule);
  const raw = rule.fractional;
  if (keys.length !== 1 || keys[0] !== "fractional" || !Array.isArray(raw) || raw.length < 2) return null;
  const defined = variants ?? {};
  const buckets = [];
  for (const bucket of raw) {
    if (!Array.isArray(bucket) || bucket.length !== 2) return null;
    const [variant, percentage] = bucket;
    if (typeof variant !== "string" || !Object.hasOwn(defined, variant) || buckets.some((b) => b.variant === variant)) return null;
    if (!Number.isInteger(percentage) || percentage < 0 || percentage > 100) return null;
    buckets.push({ variant, percentage });
  }
  return buckets.reduce((sum, b) => sum + b.percentage, 0) === 100 ? buckets : null;
}

/**
 * The boolean rollout percentage N when `variants` are a boolean flag's (every value true or false) and `buckets` are
 * exactly [on N, off 100 - N], else null (the server's `rollout.percentage`): a string flag's on/off split is a split.
 */
function onPercentage(buckets, variants) {
  const values = Object.values(variants ?? {});
  const booleanFlag = values.length > 0 && values.every((v) => typeof v === "boolean");
  return booleanFlag && buckets?.length === 2 && buckets[0].variant === "on" && buckets[1].variant === "off" ? buckets[0].percentage : null;
}

/** "34/33/33": a split's weights in rule order. */
function splitWeights(buckets) {
  return buckets.map((b) => b.percentage).join("/");
}

/**
 * "targeting added/removed/changed", "rollout N% → M%" between two boolean rollouts, or "split 50/50 → 34/33/33"
 * between two other splits; null when unchanged.
 */
function describeTargetingChange(before, after) {
  const was = ruleOf(before), now = ruleOf(after);
  if (sameRule(was, now)) return null;
  if (was === null) return "targeting added";
  if (now === null) return "targeting removed";
  const from = splitBuckets(was, before.variants), to = splitBuckets(now, after.variants);
  if (from === null || to === null) return "targeting changed";
  const fromOn = onPercentage(from, before.variants), toOn = onPercentage(to, after.variants);
  if (fromOn !== null && toOn !== null) return `rollout ${fromOn}% → ${toOn}%`;
  return `split ${splitWeights(from)} → ${splitWeights(to)}`;
}

// ---------------------------------------------------------------- variant rows

/**
 * Editable variant rows (a name and a typed value input each, removable) with a default-variant picker, shared by the
 * create form and the flag page's variants card. Values are read as `typeOf()`: text for strings, a number box for
 * numbers, a JSON textarea for objects. The picker keeps the admin's pick while it still exists; until they pick (or once
 * it is gone) the first variant is the default. An `initialDefault` (null for none) counts as picked.
 * Returns `list` and `picker` to place, `add(name, value)` (returns the row), `retype()` after the type changes, `count`,
 * `names()`, `value()` (`{ name: value }`; throws Error naming the variant whose value can't be read) and
 * `defaultVariant()` (null for none); `onChange` is called after every edit.
 */
function variantRows(typeOf, { initialDefault, onChange } = {}) {
  const rows = [];
  const list = h("div", { class: "stack variant-rows" });
  const picker = h("select", { "aria-label": "Default variant" });
  let choice = initialDefault === undefined ? undefined : (initialDefault ?? ""); // undefined until picked; "" is "none"
  const changed = () => { drawDefaults(); onChange?.(); };
  picker.addEventListener("change", () => { choice = picker.value; onChange?.(); });

  /** A value as input text: strings as they are, numbers as digits, objects as indented JSON. */
  function valueText(value) {
    if (value === undefined) return "";
    return typeof value === "string" ? value : JSON.stringify(value, null, typeof value === "object" ? 2 : undefined);
  }

  function valueInput(text = "") {
    const common = { "aria-label": "Variant value", spellcheck: "false", autocomplete: "off", oninput: () => onChange?.() };
    if (typeOf() === "number") return h("input", { ...common, type: "number", step: "any", required: true, value: text });
    if (typeOf() === "object") return h("textarea", { ...common, class: "code", rows: 3, placeholder: '{ "rows": 3 }' }, text || "{}");
    return h("input", { ...common, value: text, placeholder: "value" });
  }

  function add(name = "", value = undefined) {
    const row = { name: h("input", { value: name, placeholder: "name", "aria-label": "Variant name", maxlength: 128, required: true,
      spellcheck: "false", autocomplete: "off", oninput: changed }) };
    row.value = valueInput(valueText(value));
    row.el = h("div", { class: "variant-row" }, row.name, row.value,
      h("button", { type: "button", title: "Remove variant", "aria-label": "Remove variant", onclick: () => {
        rows.splice(rows.indexOf(row), 1);
        row.el.remove();
        changed();
      } }, "×"));
    rows.push(row);
    list.append(row.el);
    changed();
    return row;
  }

  function retype() {
    for (const row of rows) { const next = valueInput(); row.value.replaceWith(next); row.value = next; }
  }

  const names = () => [...new Set(rows.map((r) => r.name.value).filter(Boolean))];

  function drawDefaults() {
    const all = names();
    const selected = choice === "" || all.includes(choice) ? choice : (all[0] ?? "");
    picker.replaceChildren(
      ...all.map((n) => h("option", { value: n, selected: n === selected }, n)),
      h("option", { value: "", selected: selected === "" }, "(none: each app gets its code default)"));
  }

  function value() {
    const variants = {};
    for (const r of rows) {
      const name = r.name.value;
      if (Object.hasOwn(variants, name)) throw new Error(`Variant '${name}' is defined more than once.`);
      if (typeOf() === "number") {
        const n = Number(r.value.value);
        if (r.value.value.trim() === "" || !Number.isFinite(n)) throw new Error(`Variant '${name}' needs a number.`);
        variants[name] = n;
      } else if (typeOf() === "object") {
        try { variants[name] = JSON.parse(r.value.value); } catch (e) { throw new Error(`Variant '${name}' is not valid JSON: ${e.message}`); }
      } else {
        variants[name] = r.value.value;
      }
    }
    return variants;
  }

  drawDefaults();
  return { list, picker, add, retype, names, value, defaultVariant: () => picker.value || null, get count() { return rows.length; } };
}

// ---------------------------------------------------------------- create

async function renderCreate() {
  let existing = [];
  try {
    existing = (await api.listFlags()).map((f) => f.key);
  } catch (e) {
    showNotice(`Could not load existing flags to check for near-duplicates: ${e.message}`);
  }

  const key = h("input", { required: true, maxlength: 128, autocomplete: "off", spellcheck: "false", placeholder: "e.g. checkout.new-flow" });
  const keyHelp = h("p", { class: "small muted" }, "Letters, digits, '.', '_' and '-'; case-sensitive.");
  const duplicateWarning = h("p", { class: "warning", hidden: true });
  const enabled = h("input", { type: "checkbox" });
  const description = h("textarea", { rows: 2, maxlength: 2000 });
  const tags = h("input", { placeholder: "comma, separated, tags", spellcheck: "false" });
  const submit = h("button", { type: "submit", class: "primary" }, "Create flag");

  // Type: boolean flags are on/off (the Enabled box); other types get named variants and a default-variant picker.
  const type = h("select", { onchange: () => { rows.retype(); drawType(); } },
    [["boolean", "Boolean (on/off)"], ["string", "String"], ["number", "Number"], ["object", "Object (JSON)"]]
      .map(([value, label]) => h("option", { value }, label)));
  const enabledLabel = h("label", { class: "inline" }, enabled, "Enabled");
  // Values are read as the selected type; until the admin picks a default variant, the first one is it.
  const rows = variantRows(() => type.value);

  const variantsSection = h("fieldset", { class: "stack variants-editor" },
    h("legend", {}, "Variants"),
    h("p", { class: "small muted" }, "Named values the flag can serve. All share the flag's type, which can't change after creation."),
    rows.list,
    h("div", {}, h("button", { type: "button", onclick: () => rows.add().name.focus() }, "Add variant")),
    h("label", {}, "Default variant", rows.picker),
    h("p", { class: "small muted" }, "Served when no targeting rule matches."));

  function drawType() {
    const typed = type.value !== "boolean";
    enabledLabel.hidden = typed;
    variantsSection.hidden = !typed;
    variantsSection.disabled = !typed; // hidden rows must not block submitting a boolean flag
    if (typed && rows.count === 0) rows.add("default");
  }

  function check() {
    const problem = key.value ? keyProblem(key.value) : null;
    keyHelp.textContent = problem ?? "Letters, digits, '.', '_' and '-'; case-sensitive.";
    keyHelp.className = problem ? "small error-text" : "small muted";
    key.setCustomValidity(problem ?? "");

    const exact = existing.includes(key.value);
    const similar = key.value ? nearDuplicates(key.value, existing) : [];
    duplicateWarning.hidden = !exact && similar.length === 0;
    duplicateWarning.replaceChildren(
      ...(exact
        ? ["A flag with this key already exists: ", h("a", { href: flagHref(key.value) }, key.value)]
        : ["Similar keys already exist (they differ only by case or separators): ",
          ...similar.flatMap((k, i) => [i ? ", " : "", h("a", { href: flagHref(k) }, k)]),
          ". Check you are not creating a near-duplicate."]));
    submit.disabled = Boolean(problem) || exact;
  }
  key.addEventListener("input", check);

  const form = h("form", {
    class: "card stack narrow-form",
    onsubmit: async (e) => {
      e.preventDefault();
      const problem = keyProblem(key.value);
      if (problem) { showNotice(problem); return; }
      const typed = type.value !== "boolean";
      let variants;
      try { variants = typed ? rows.value() : undefined; } catch (err) { showNotice(err.message); return; }
      submit.disabled = true;
      try {
        const created = await api.createFlag({
          key: key.value,
          enabled: typed ? undefined : enabled.checked,
          description: description.value.trim() || undefined,
          tags: parseTags(tags.value),
          // Object values are checked server-side (they must be JSON objects); its problem details show as the notice.
          variants: typed ? variants : undefined,
          defaultVariant: typed ? rows.defaultVariant() : undefined,
        });
        showNotice(`${created.key} created.`, "success");
        location.hash = flagHref(created.key);
      } catch (err) {
        showNotice(err.message);
        submit.disabled = false;
      }
    },
  },
  h("label", {}, "Key", key), keyHelp, duplicateWarning,
  h("label", {}, "Type", type),
  enabledLabel,
  variantsSection,
  h("label", {}, "Description", description),
  h("label", {}, "Tags", tags),
  h("div", { class: "actions" }, submit, h("a", { href: "#/", class: "button" }, "Cancel")));

  drawType();
  setTimeout(() => key.focus());
  return [h("p", {}, h("a", { href: "#/" }, "← Flags")), h("h1", {}, "New flag"), form];
}

// ---------------------------------------------------------------- segments

/** Above this many keys, a list segment's editor warns that each evaluation scans the list. */
const LARGE_LIST = 1000;

const BACK_TO_SEGMENTS = ["#/segments", "Back to segments"];

/** Tooltip on controls disabled because the segment is archived. */
const SEGMENT_ARCHIVED_LOCKED = "Restore the segment to change its rule";

const segmentListState = { q: "", tag: "", archived: "active" };

async function renderSegmentList() {
  let segments;
  try {
    segments = await api.listSegments();
  } catch (e) {
    return [h("h1", {}, "Segments"), errorView(e)];
  }

  const tags = [...new Set(segments.flatMap((sg) => sg.tags))].sort();
  const tbody = h("tbody");
  const count = h("span", { class: "muted" });

  const select = (name, label, options) => h("label", {}, label,
    h("select", { onchange: (e) => { segmentListState[name] = e.target.value; draw(); } },
      options.map(([value, text]) => h("option", { value, selected: value === segmentListState[name] }, text))));

  function matches(sg) {
    const q = segmentListState.q.trim().toLowerCase();
    if (q && !sg.key.toLowerCase().includes(q) && !(sg.description ?? "").toLowerCase().includes(q)) return false;
    if (segmentListState.tag && !sg.tags.includes(segmentListState.tag)) return false;
    if (segmentListState.archived === "active" && sg.archived) return false;
    if (segmentListState.archived === "archived" && !sg.archived) return false;
    return true;
  }

  /** Opens the flag list filtered to the flags using `key`. */
  const showUsers = (key) => { listState.segment = key; listState.archived = "all"; };

  function row(sg) {
    return h("tr", { class: sg.archived ? "is-archived" : undefined },
      h("td", {}, h("a", { href: segmentHref(sg.key), class: "key" }, sg.key), " ",
        h("span", { class: "badges" },
          sg.archived && h("span", { class: "badge archived", title: "Archived: keeps resolving, but can't be newly referenced" }, "archived"),
          sg.source === "code" && h("span", { class: "badge code", title: "Defined in code" }, "code")),
        sg.description && h("div", { class: "muted small" }, sg.description)),
      h("td", { class: "small" }, sg.keys ? plural(sg.keys.length, "key") : "rule"),
      h("td", {}, sg.tags.map((t) => h("span", { class: "tag" }, t))),
      h("td", {}, sg.usedBy === 0
        ? h("span", { class: "muted" }, "no flags")
        : h("a", { href: "#/", onclick: () => showUsers(sg.key) }, plural(sg.usedBy, "flag"))));
  }

  function draw() {
    const shown = segments.filter(matches);
    tbody.replaceChildren(...(shown.length
      ? shown.map(row)
      : [h("tr", {}, h("td", { colspan: 4, class: "empty" }, segments.length ? "No segments match these filters." : "No segments yet."))]));
    count.textContent = `${shown.length} of ${plural(segments.length, "segment")}`;
  }

  const search = h("input", {
    type: "search", placeholder: "Search keys and descriptions", value: segmentListState.q, "aria-label": "Search",
    oninput: (e) => { segmentListState.q = e.target.value; draw(); },
  });

  draw();
  setTimeout(() => search.focus());
  return [
    h("div", { class: "title-row" }, h("h1", {}, "Segments"), count, h("a", { href: "#/segments/new", class: "button primary" }, "New segment")),
    h("p", { class: "muted small" }, "Reusable audiences: define one once and reference it from any flag's targeting with ",
      h("code", {}, '{"$ref": "<segment>"}'), ". Editing a segment changes every flag that uses it."),
    h("div", { class: "filters" },
      search,
      select("tag", "Tag", [["", "Any"], ...tags.map((t) => [t, t])]),
      select("archived", "Show", [["active", "Active"], ["archived", "Archived"], ["all", "All"]])),
    h("table", { class: "flags" },
      h("thead", {}, h("tr", {}, h("th", {}, "Segment"), h("th", {}, "Rule"), h("th", {}, "Tags"), h("th", {}, "Used by"))),
      tbody),
  ];
}

/**
 * A segment rule editor with two modes. List: targeting keys one per line, saved as the canonical
 * {"in":[{"var":"targetingKey"},[…]]} rule; offered only when the saved rule is exactly that shape (the server reports it
 * as `keys`) or for a new segment. Rule: a JSON editor. Switching to Rule shows the list as JSON; switching back is
 * allowed only while that JSON is untouched, so a hand-written rule is never reinterpreted. Returns `el`, `rule()` (throws
 * SyntaxError for invalid JSON, Error for an empty rule) and `dirty()`.
 */
function segmentRuleEditor({ keys, rule, disabled, onChange }) {
  const listable = Array.isArray(keys);
  let mode = listable ? "list" : "rule";
  let generated = null; // the JSON text a list switch produced, while untouched
  const listKeys = () => [...new Set(list.value.split("\n").map((k) => k.trim()).filter(Boolean))];
  const listRule = () => ({ in: [{ var: "targetingKey" }, listKeys()] });

  const warning = h("p", { class: "warning", hidden: true });
  const list = h("textarea", {
    class: "code", rows: 10, spellcheck: "false", autocomplete: "off", disabled, "aria-label": "Targeting keys, one per line",
    placeholder: "user-1\nuser-2", oninput: () => { warn(); onChange?.(); },
  }, (keys ?? []).join("\n"));
  const json = h("textarea", {
    class: "code", rows: 10, spellcheck: "false", autocomplete: "off", disabled, "aria-label": "Segment rule (JSON Logic)",
    placeholder: '{"in": [{"var": "country"}, ["FR", "DE"]]}', oninput: () => onChange?.(),
  }, rule === undefined ? "" : JSON.stringify(rule, null, 2));
  const initial = { list: list.value, json: json.value };

  function warn() {
    const n = listKeys().length;
    warning.hidden = n <= LARGE_LIST;
    warning.textContent = `${n} keys: every evaluation scans the list, so very long lists slow down the flags that use this segment. A rule on an attribute (such as a plan or a role) scales better.`;
  }

  const tabs = h("div", { class: "actions", role: "tablist" });
  const body = h("div", { class: "stack" });

  function draw() {
    const canList = listable && (mode === "list" || json.value === generated);
    tabs.replaceChildren(
      h("button", { type: "button", role: "tab", class: mode === "list" ? "primary" : undefined, "aria-selected": String(mode === "list"),
        disabled: !canList, title: canList ? undefined : "Only a rule that is exactly a list of targeting keys can be edited as a list",
        onclick: () => switchTo("list") }, "List"),
      h("button", { type: "button", role: "tab", class: mode === "rule" ? "primary" : undefined, "aria-selected": String(mode === "rule"),
        onclick: () => switchTo("rule") }, "Rule"));
    body.replaceChildren(...(mode === "list"
      ? [h("p", { class: "small muted" }, "Targeting keys, one per line: a user is in the segment when their targeting key is listed."), list, warning]
      : [h("p", { class: "small muted" }, "A JSON Logic condition using flagd's operators: a user is in the segment when it is truthy. It can't reference another segment."), json]));
  }

  function switchTo(next) {
    if (next === mode) return;
    if (next === "rule") {
      json.value = generated = JSON.stringify(listRule(), null, 2);
    }
    mode = next;
    draw();
    onChange?.();
  }
  json.addEventListener("input", () => { if (generated !== null && json.value !== generated) { generated = null; draw(); } });

  warn();
  draw();
  return {
    el: h("div", { class: "stack" }, tabs, body),
    rule() {
      if (mode === "list") return listRule();
      if (json.value.trim() === "") throw new Error("A segment needs a rule.");
      return JSON.parse(json.value);
    },
    dirty: () => list.value !== initial.list || json.value !== initial.json,
  };
}

/**
 * The "Test against a context" card: runs the saved rule (when `segment` exists) or the editor's draft for a context
 * typed here, through the side-effect-free evaluate endpoint.
 */
function segmentTestCard(key, editor, saved) {
  const targetingKey = h("input", { placeholder: "e.g. user-123", spellcheck: "false", autocomplete: "off" });
  const attributes = h("textarea", { class: "code", rows: 3, spellcheck: "false", autocomplete: "off", placeholder: '{ "country": "FR", "plan": "pro" }' });
  const result = h("div", { class: "tryit-result", hidden: true });

  function context() {
    const ctx = attributes.value.trim() === "" ? {} : JSON.parse(attributes.value);
    if (typeof ctx !== "object" || ctx === null || Array.isArray(ctx)) throw new Error("Attributes must be a JSON object.");
    if (targetingKey.value.trim()) ctx.targetingKey = targetingKey.value.trim();
    return ctx;
  }

  async function run(which, e) {
    const button = e.currentTarget;
    const show = (...content) => { result.replaceChildren(h("h3", {}, which === "draft" ? "Draft" : "Saved"), ...content); result.hidden = false; };
    let ctx, rule;
    try {
      ctx = context();
      rule = which === "draft" ? editor.rule() : undefined;
    } catch (err) {
      show(h("p", { class: "small error-text" }, ruleError(err)));
      return;
    }
    button.disabled = true;
    try {
      const r = await api.evaluateSegment(key || "draft", ctx, rule);
      show(
        h("p", {}, r.error
          ? h("span", { class: "error-text" }, `The rule failed: ${r.error}`)
          : h("strong", { class: r.matches ? "ok-text" : undefined }, r.matches ? "In the segment" : "Not in the segment")),
        h("p", { class: "small muted" }, "Result: ", r.result === null ? "null" : h("code", {}, r.result)));
    } catch (err) {
      show(h("p", { class: "small error-text" }, ruleError(err)));
    } finally {
      button.disabled = false;
    }
  }

  return h("section", { class: "card stack tryit" },
    h("h2", {}, "Test against a context"),
    h("p", { class: "small muted" }, "Check who is in the segment. Nothing is saved."),
    h("label", {}, "Targeting key", targetingKey),
    h("label", {}, "Attributes (JSON)", attributes),
    h("div", { class: "actions" },
      saved && h("button", { type: "button", class: "primary", onclick: (e) => run("saved", e) }, "Test saved"),
      h("button", { type: "button", onclick: (e) => run("draft", e) }, saved ? "Test draft" : "Test")),
    result);
}

/** An error to show inline: for a segment in use, the message with links to the flags that reference it. */
function segmentError(e) {
  const flags = e instanceof ApiError ? e.problem.flags : undefined;
  return Array.isArray(flags) && flags.length > 0
    ? [h("strong", {}, `${e.problem.title}: `), e.message, " ", h("span", {}, "Flags: ", links(flags, flagHref), ".")]
    : ruleError(e);
}

/** A short summary of a segment audit entry, with the raw documents behind a disclosure. */
function describeSegmentChange(entry) {
  const before = entry.before, after = entry.after;
  const changes = [];
  if (before && after) {
    if (JSON.stringify(before.rule) !== JSON.stringify(after.rule)) changes.push("rule changed");
    const bm = before.metadata ?? {}, am = after.metadata ?? {};
    if ((bm.description ?? "") !== (am.description ?? "")) changes.push("description");
    if ((bm.tags ?? "") !== (am.tags ?? "")) changes.push(`tags → ${am.tags || "none"}`);
  }
  return h("details", {},
    h("summary", {}, changes.join(", ") || "details"),
    h("pre", {}, JSON.stringify({ before, after }, null, 2)));
}

async function renderSegmentDetail(key) {
  let segment, audit;
  try {
    [segment, audit] = await Promise.all([api.getSegment(key), api.segmentAudit(key)]);
  } catch (e) {
    return [h("p", {}, h("a", { href: "#/segments" }, "← Segments")), errorView(e, BACK_TO_SEGMENTS)];
  }

  const reload = () => route();
  const locked = segment.archived ? SEGMENT_ARCHIVED_LOCKED : undefined;

  // Rule
  const ruleMessage = h("p", { role: "status", class: "small error-text", hidden: true });
  const showRuleError = (content) => { ruleMessage.replaceChildren(h("span", {}, content)); ruleMessage.hidden = false; };
  const editor = segmentRuleEditor({ keys: segment.keys, rule: segment.rule, disabled: segment.archived, onChange: () => { ruleMessage.hidden = true; } });
  const ruleCard = h("form", {
    class: "card stack",
    onsubmit: async (e) => {
      e.preventDefault();
      let rule;
      try { rule = editor.rule(); } catch (err) { showRuleError(ruleError(err)); return; }
      if (await saveInline(() => api.setSegmentRule(segment.key, segment.version, rule), { button: e.submitter, showError: showRuleError, reload })) {
        showNotice(segment.usedBy.length ? `Rule saved: ${plural(segment.usedBy.length, "flag")} now use${segment.usedBy.length === 1 ? "s" : ""} it.` : "Rule saved.", "success");
        await reload();
      }
    },
  },
  h("h2", {}, "Rule"),
  segment.archived && h("p", { class: "small muted" }, "Archived: the rule keeps resolving for the flags that reference it, but can't be changed until the segment is restored."),
  editor.el,
  ruleMessage,
  h("div", { class: "actions" }, h("button", { type: "submit", class: "primary", disabled: segment.archived, title: locked }, "Save rule")));

  // Details
  const description = h("textarea", { rows: 2, maxlength: 2000 }, segment.description ?? "");
  const tags = h("input", { value: segment.tags.join(", "), placeholder: "comma, separated, tags", spellcheck: "false" });
  const metadata = h("form", {
    class: "card stack",
    onsubmit: async (e) => {
      e.preventDefault();
      const result = await write(() => api.updateSegmentMetadata(segment.key, segment.version, {
        description: description.value.trim(),
        tags: parseTags(tags.value),
      }), reload);
      if (result) { showNotice("Saved.", "success"); await reload(); }
    },
  },
  h("h2", {}, "Details"),
  h("label", {}, "Description", description),
  h("label", {}, "Tags", tags),
  h("div", {}, h("button", { type: "submit", class: "primary" }, "Save")));

  // Lifecycle: refusals (in use, …) are shown here, with links to the flags concerned.
  const lifecycleMessage = h("p", { role: "status", class: "small error-text", hidden: true });
  async function lifecycleAction(action, success, after) {
    try {
      await action();
    } catch (e) {
      if (e instanceof ApiError && e.isVersionConflict) await conflictPrompt(e, reload);
      else { lifecycleMessage.replaceChildren(h("span", {}, segmentError(e))); lifecycleMessage.hidden = false; }
      return;
    }
    showNotice(success, "success");
    await (after ?? reload)();
  }
  const live = segment.usedBy.filter((u) => !u.archived);
  const lifecycle = h("section", { class: "card stack" }, h("h2", {}, "Lifecycle"),
    segment.archived
      ? [
        h("p", {}, "This segment is archived: it keeps resolving for archived flags that reference it, but can't be newly referenced. Restore it to use it again, or purge it to delete it permanently (its audit history is kept)."),
        h("div", { class: "actions" },
          h("button", { onclick: () => lifecycleAction(() => api.restoreSegment(segment.key, segment.version), "Restored.") }, "Restore"),
          h("button", {
            class: "danger",
            onclick: async () => {
              const ok = await confirmDialog({
                title: `Purge ${segment.key}?`,
                detail: segment.usedBy.length
                  ? `It can't be purged while flags reference it (${segment.usedBy.map((u) => u.key).join(", ")}), archived ones included.`
                  : "The segment is deleted permanently. Its audit history is kept.",
                ok: "Purge",
                typed: segment.key,
              });
              if (ok) await lifecycleAction(() => api.purgeSegment(segment.key, segment.version), `${segment.key} purged.`, () => { location.hash = "#/segments"; });
            },
          }, "Purge…")),
      ]
      : [
        h("p", {}, "Archive a segment that is no longer needed: it keeps resolving for archived flags, but can't be newly referenced. A segment that live flags reference can't be archived."),
        h("div", { class: "actions" },
          h("button", {
            onclick: async () => {
              const ok = await confirmDialog({
                title: `Archive ${segment.key}?`,
                detail: live.length
                  ? `Live flags reference it (${live.map((u) => u.key).join(", ")}); remove the references first.`
                  : "It can't be newly referenced or edited until it is restored. Flags that reference it are unaffected.",
                ok: "Archive",
              });
              if (ok) await lifecycleAction(() => api.archiveSegment(segment.key, segment.version), "Archived.");
            },
          }, "Archive…")),
      ],
    lifecycleMessage);

  const usedBy = h("section", { class: "card" }, h("h2", {}, "Used by"),
    segment.usedBy.length === 0
      ? h("p", { class: "muted" }, "No flag references this segment.")
      : h("ul", {}, segment.usedBy.map((u) => h("li", {}, h("a", { href: flagHref(u.key), class: "key" }, u.key),
        u.archived && [" ", h("span", { class: "badge archived" }, "archived")]))));

  const history = h("section", { class: "card" }, h("h2", {}, "Audit history"),
    audit.length === 0
      ? h("p", { class: "muted" }, "No recorded changes.")
      : h("table", { class: "audit" },
        h("thead", {}, h("tr", {}, h("th", {}, "When"), h("th", {}, "Action"), h("th", {}, "By"), h("th", {}, "Application"), h("th", {}, "Version"), h("th", {}, "Change"))),
        h("tbody", {}, audit.map((entry) => h("tr", {},
          h("td", {}, time(entry.timestamp)),
          h("td", {}, h("span", { class: `badge action-${entry.action}` }, entry.action)),
          h("td", {}, entry.actor ?? h("span", { class: "muted" }, "system")),
          h("td", {}, entry.applicationName ?? ""),
          h("td", {}, String(entry.version)),
          h("td", {}, describeSegmentChange(entry)))))));

  return [
    h("p", {}, h("a", { href: "#/segments" }, "← Segments")),
    h("div", { class: "title-row" }, h("h1", { class: "key" }, segment.key),
      h("span", { class: "badges" },
        segment.archived && h("span", { class: "badge archived" }, "archived"),
        segment.source === "code" && h("span", { class: "badge code", title: "Defined in code" }, "code"))),
    h("p", { class: "muted small" },
      `Version ${segment.version} · source ${segment.source} · created `, time(segment.createdAt), segment.createdBy ? ` by ${segment.createdBy}` : "",
      " · updated ", time(segment.updatedAt), segment.updatedBy ? ` by ${segment.updatedBy}` : ""),
    h("p", { class: "small" }, "Reference it from a flag's targeting with ", h("code", {}, JSON.stringify({ $ref: segment.key })), "."),
    ruleCard,
    segmentTestCard(segment.key, editor, true),
    h("div", { class: "grid" }, metadata, lifecycle),
    usedBy,
    history,
  ];
}

async function renderSegmentCreate() {
  let existing = [];
  try {
    existing = (await api.listSegments()).map((sg) => sg.key);
  } catch (e) {
    showNotice(`Could not load existing segments to check for near-duplicates: ${e.message}`);
  }

  const key = h("input", { required: true, maxlength: 128, autocomplete: "off", spellcheck: "false", placeholder: "e.g. beta-testers" });
  const keyHelp = h("p", { class: "small muted" }, "Letters, digits, '.', '_' and '-'; case-sensitive.");
  const duplicateWarning = h("p", { class: "warning", hidden: true });
  const description = h("textarea", { rows: 2, maxlength: 2000 });
  const tags = h("input", { placeholder: "comma, separated, tags", spellcheck: "false" });
  const submit = h("button", { type: "submit", class: "primary" }, "Create segment");
  const editor = segmentRuleEditor({ keys: [] });

  function check() {
    const problem = key.value ? keyProblem(key.value) : null;
    keyHelp.textContent = problem ?? "Letters, digits, '.', '_' and '-'; case-sensitive.";
    keyHelp.className = problem ? "small error-text" : "small muted";
    key.setCustomValidity(problem ?? "");

    const exact = existing.includes(key.value);
    const similar = key.value ? nearDuplicates(key.value, existing) : [];
    duplicateWarning.hidden = !exact && similar.length === 0;
    duplicateWarning.replaceChildren(
      ...(exact
        ? ["A segment with this key already exists: ", h("a", { href: segmentHref(key.value) }, key.value)]
        : ["Similar keys already exist (they differ only by case or separators): ", ...links(similar, segmentHref), ". Check you are not creating a near-duplicate."]));
    submit.disabled = Boolean(problem) || exact;
  }
  key.addEventListener("input", check);

  const form = h("form", {
    class: "card stack narrow-form",
    onsubmit: async (e) => {
      e.preventDefault();
      const problem = keyProblem(key.value);
      if (problem) { showNotice(problem); return; }
      let rule;
      try { rule = editor.rule(); } catch (err) { showNotice(err.message); return; }
      submit.disabled = true;
      try {
        const created = await api.createSegment({ key: key.value, rule, description: description.value.trim() || undefined, tags: parseTags(tags.value) });
        showNotice(`${created.key} created.`, "success");
        location.hash = segmentHref(created.key);
      } catch (err) {
        showNotice(err.message);
        submit.disabled = false;
      }
    },
  },
  h("label", {}, "Key", key), keyHelp, duplicateWarning,
  h("div", { class: "stack" }, h("strong", {}, "Rule"), editor.el),
  h("label", {}, "Description", description),
  h("label", {}, "Tags", tags),
  h("div", { class: "actions" }, submit, h("a", { href: "#/segments", class: "button" }, "Cancel")));

  setTimeout(() => key.focus());
  return [h("p", {}, h("a", { href: "#/segments" }, "← Segments")), h("h1", {}, "New segment"), form, segmentTestCard(null, editor, false)];
}

// ---------------------------------------------------------------- router

let generation = 0;

async function route() {
  const current = ++generation;
  const hash = location.hash.replace(/^#/, "") || "/";
  let content;
  let section = "list";
  const segments = await segmentsSupported();
  if (hash === "/new") {
    section = "new";
    content = await renderCreate();
  } else if (segments && hash === "/segments") {
    section = "segments";
    content = await renderSegmentList();
  } else if (segments && hash === "/segments/new") {
    section = "segments";
    content = await renderSegmentCreate();
  } else if (segments && hash.startsWith("/segments/")) {
    section = "segments";
    content = await renderSegmentDetail(decodeURIComponent(hash.slice("/segments/".length)));
  } else if (hash.startsWith("/flags/")) {
    content = await renderDetail(decodeURIComponent(hash.slice("/flags/".length)));
  } else {
    content = await renderList();
  }
  if (current !== generation) return; // a newer navigation won
  document.getElementById("segments-nav").hidden = !segments;
  for (const link of document.querySelectorAll("[data-nav]")) {
    link.toggleAttribute("aria-current", link.dataset.nav === section);
  }
  // Views return conditional children (false when absent); replaceChildren doesn't drop them as h() does.
  view.replaceChildren(...content.flat().filter(Boolean));
}

window.addEventListener("hashchange", () => {
  if (!notice.classList.contains("success")) showNotice("");
  route();
});
route();
