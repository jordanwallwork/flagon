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

function badges(flag) {
  return h("span", { class: "badges" },
    flag.archived && h("span", { class: "badge archived", title: "Archived: evaluates to the code default" }, "archived"),
    flag.state === "disabled" && h("span", { class: "badge warn", title: "flagd state DISABLED: evaluates to the code default" }, "disabled"),
    flag.source === "auto" && h("span", { class: "badge auto", title: "Created automatically on first evaluation" }, "auto"),
    flag.source === "code" && h("span", { class: "badge code", title: "Defined in code" }, "code"),
    !flag.archived && isStale(flag) && h("span", { class: "badge stale", title: `Not evaluated in the last ${staleDays()} days` }, "stale"),
    flag.usage?.hasDefaultMismatch && h("span", { class: "badge mismatch", title: "Applications pass different code defaults" }, "default mismatch"),
    targetingBadge(flag),
  );
}

/** "rollout N%" for a simple percentage rollout, "targeted" for any other rule, nothing without one. */
function targetingBadge(flag) {
  if (flag.rollout) return h("span", { class: "badge targeting", title: "Percentage rollout: this share of users gets on" }, `rollout ${flag.rollout.percentage}%`);
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
  return h("label", { class: "switch", title: flag.archived ? "Restore the flag to change it" : undefined }, input, h("span", { class: "slider" }));
}

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

function errorView(e) {
  const message = e instanceof ApiError && e.status === 404 ? "Not found." : e.message;
  return h("div", { class: "empty" }, h("p", {}, message), h("p", {}, h("a", { href: "#/" }, "Back to flags")));
}

// ---------------------------------------------------------------- list

const listState = { q: "", tag: "", state: "", source: "", archived: "active", hygiene: "", targeting: "" };

async function renderList() {
  let flags;
  try {
    flags = await api.listFlags();
  } catch (e) {
    return [h("h1", {}, "Flags"), errorView(e)];
  }

  const tags = [...new Set(flags.flatMap((f) => f.tags))].sort();
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
    if (listState.state === "on" && !f.enabled) return false;
    if (listState.state === "off" && f.enabled) return false;
    if (listState.source && f.source !== listState.source) return false;
    if (listState.archived === "active" && f.archived) return false;
    if (listState.archived === "archived" && !f.archived) return false;
    if (listState.hygiene === "stale" && (f.archived || !isStale(f))) return false;
    if (listState.hygiene === "mismatch" && !f.usage?.hasDefaultMismatch) return false;
    if (listState.targeting === "targeted" && !hasTargeting(f)) return false;
    if (listState.targeting === "rollout" && !f.rollout) return false;
    if (listState.targeting === "none" && hasTargeting(f)) return false;
    return true;
  }

  function row(f) {
    const apps = f.usage?.applications ?? [];
    return h("tr", { class: f.archived ? "is-archived" : undefined },
      h("td", {}, toggle(f, (enabled) => write(async () => {
        await api.setEnabled(f.key, f.version, enabled);
        showNotice(`${f.key} ${enabled ? "enabled" : "disabled"}.`, "success");
        await refresh();
        return true;
      }, refresh))),
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
    select("state", "State", [["", "Any"], ["on", "On"], ["off", "Off"]]),
    select("source", "Source", [["", "Any"], ["manual", "Manual"], ["auto", "Auto"], ["code", "Code"]]),
    select("archived", "Show", [["active", "Active"], ["archived", "Archived"], ["all", "All"]]),
    select("hygiene", "Hygiene", [["", "Any"], ["stale", "Stale"], ["mismatch", "Default mismatch"]]),
    select("targeting", "Targeting", [["", "Any"], ["targeted", "Targeted"], ["rollout", "Rollout"], ["none", "None"]]),
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
      h("thead", {}, h("tr", {}, h("th", { class: "narrow" }, "On"), h("th", {}, "Flag"), h("th", {}, "Tags"), h("th", {}, "Last evaluated"))),
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
 * The flagd document the server evaluates for an unsaved rule: the stored flag's state and default variant with the
 * console's boolean variants. Used by Validate and by draft evaluation elsewhere on the detail view.
 */
function draftDocument(flag, rule) {
  const document = { state: flag.state === "enabled" ? "ENABLED" : "DISABLED", variants: { on: true, off: false }, defaultVariant: flag.defaultVariant };
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
 * The Targeting card. Returns the card and `draft`, the editor's unsaved state, for other cards on the detail view:
 * `draft.editing`, `draft.dirty`, `draft.text`, `draft.document()` (the flagd document to evaluate; throws SyntaxError
 * for invalid JSON) and `draft.subscribe(fn)` (called whenever any of those change; returns an unsubscribe function).
 */
function targetingCard(flag, reload) {
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

  const locked = flag.archived ? "Restore the flag to change it" : undefined;

  function show() {
    editor = null;
    // Only the server's recogniser (flag.rollout) decides what is a rollout; any other rule is never rewritten by the slider.
    const custom = hasTargeting(flag) && !flag.rollout;
    card.replaceChildren(
      h("h2", {}, "Targeting"),
      custom
        ? [h("p", { class: "small muted" }, "Custom rule: edit as JSON."), h("pre", { class: "rule" }, saved)]
        : [!flag.rollout && h("p", { class: "muted" }, "No targeting: everyone gets the default variant."), rolloutControl()],
      h("div", { class: "actions" },
        h("button", { disabled: flag.archived, title: locked, onclick: () => edit(saved) }, hasTargeting(flag) ? "Edit JSON" : "Add rule"),
        custom && h("button", { class: "danger", disabled: flag.archived, title: locked, onclick: clear }, "Clear…")),
    );
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
      e.target.disabled = true;
      try {
        const result = await api.evaluate(flag.key, {}, draftDocument(flag, p.rule));
        status(["Valid rule. With an empty context it returns ", h("code", {}, result.variant ?? JSON.stringify(result.value)), ` (${result.reason}).`], "ok");
      } catch (err) {
        status(ruleError(err));
      } finally {
        e.target.disabled = false;
      }
    }

    async function save(e) {
      e.preventDefault();
      const p = parsed();
      if (!p) return;
      const button = e.submitter;
      if (button) button.disabled = true;
      try {
        await api.setTargeting(flag.key, flag.version, p.rule);
      } catch (err) {
        // Not write(): that reloads and would lose the draft. Errors stay next to it; only a conflict prompts.
        if (err instanceof ApiError && err.isVersionConflict) await conflictPrompt(err, reload);
        else status(ruleError(err));
        if (button?.isConnected) button.disabled = false;
        return;
      }
      showNotice(p.rule === null ? "Targeting cleared." : "Targeting saved.", "success");
      await reload();
    }

    card.replaceChildren(h("form", { class: "stack", onsubmit: save },
      h("h2", {}, "Targeting"),
      h("p", { class: "small muted" }, "A JSON Logic rule returning a variant name (", h("code", {}, "on"), " or ", h("code", {}, "off"),
        "); when it matches nothing, the default variant applies. Leave it empty for no targeting."),
      editor,
      message,
      h("div", { class: "actions" },
        h("button", { type: "submit", class: "primary" }, "Save"),
        h("button", { type: "button", onclick: format }, "Format"),
        h("button", { type: "button", onclick: validate }, "Validate"),
        h("button", { type: "button", onclick: show }, "Cancel"))));
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

    /** Clamps to an integer 0–100 and syncs both inputs (except the number box mid-typing). */
    function set(value, typing = false) {
      const parsed = Math.round(Number(value));
      n = Number.isFinite(parsed) ? Math.min(100, Math.max(0, parsed)) : n;
      range.value = n;
      if (!typing) number.value = n;
      share.textContent = `${n}%`;
      saveButton.disabled = flag.archived || n === current;
      message.hidden = true;
    }

    async function save(e) {
      e.preventDefault();
      saveButton.disabled = true;
      try {
        await api.setTargeting(flag.key, flag.version, { fractional: [["on", n], ["off", 100 - n]] });
      } catch (err) {
        if (err instanceof ApiError && err.isVersionConflict) await conflictPrompt(err, reload);
        else { message.replaceChildren(...ruleError(err)); message.hidden = false; }
        if (saveButton.isConnected) saveButton.disabled = false;
        return;
      }
      showNotice(`Rollout saved: ${n}% of users get on.`, "success");
      await reload();
    }

    async function remove() {
      const ok = await confirmDialog({
        title: `Remove the rollout for ${flag.key}?`,
        detail: "Targeting is cleared and everyone gets the default variant. The audit history keeps a copy.",
        ok: "Remove",
      });
      if (ok && await write(() => api.clearTargeting(flag.key, flag.version), reload)) {
        showNotice("Rollout removed.", "success");
        await reload();
      }
    }

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

  async function clear() {
    const ok = await confirmDialog({
      title: `Clear targeting for ${flag.key}?`,
      detail: "The rule is removed and everyone gets the default variant. The audit history keeps a copy.",
      ok: "Clear",
    });
    if (ok && await write(() => api.clearTargeting(flag.key, flag.version), reload)) {
      showNotice("Targeting cleared.", "success");
      await reload();
    }
  }

  show();
  return { card, draft };
}

// ---------------------------------------------------------------- detail

async function renderDetail(key) {
  let flag, audit;
  try {
    [flag, audit] = await Promise.all([api.getFlag(key), api.audit(key)]);
  } catch (e) {
    return [h("p", {}, h("a", { href: "#/" }, "← Flags")), errorView(e)];
  }

  const reload = () => route();
  // targeting.draft is the editor's unsaved rule, for other cards on this view to read (see targetingCard).
  const targeting = targetingCard(flag, reload);

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
          h("td", {}, h("code", {}, a.observedDefault === null || a.observedDefault === undefined ? "—" : JSON.stringify(a.observedDefault))))))),
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
      toggle(flag, async (enabled) => {
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
    targeting.card,
    usage,
    history,
  ];
}

/** A short human summary of an audit entry, with the raw documents behind a disclosure. */
function describeChange(entry) {
  const before = entry.before, after = entry.after;
  const changes = [];
  if (before && after) {
    if (before.defaultVariant !== after.defaultVariant) changes.push(`default ${before.defaultVariant} → ${after.defaultVariant}`);
    if (before.state !== after.state) changes.push(`state ${before.state} → ${after.state}`);
    const bm = before.metadata ?? {}, am = after.metadata ?? {};
    if ((bm.description ?? "") !== (am.description ?? "")) changes.push("description");
    if ((bm.tags ?? "") !== (am.tags ?? "")) changes.push(`tags → ${am.tags || "none"}`);
  } else if (after) {
    changes.push(`default ${after.defaultVariant}`);
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
    const percentage = rolloutPercentage(rule);
    const ok = await confirmDialog({
      title: `Restore this rule on ${flag.key}?`,
      detail: rule === null
        ? `The flag had no targeting ${side}, so the current rule is removed and everyone gets the default variant.`
        : `The current rule is replaced by the one in place ${side}${percentage === null ? "" : ` (rollout ${percentage}%)`}. The audit history keeps a copy of the current rule.`,
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
    title: flag.archived ? "Restore the flag to change it" : undefined,
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
 * The percentage N of a simple rollout, exactly {"fractional": [["on", N], ["off", 100 - N]]} with an
 * integer N, else null. Mirrors the server's RolloutShape (which fills `flag.rollout`); audit documents
 * carry no `rollout`, so the history needs this client-side copy.
 */
function rolloutPercentage(rule) {
  if (!rule || typeof rule !== "object" || Array.isArray(rule)) return null;
  const keys = Object.keys(rule);
  const buckets = rule.fractional;
  if (keys.length !== 1 || keys[0] !== "fractional" || !Array.isArray(buckets) || buckets.length !== 2) return null;
  const [on, off] = buckets;
  if (!Array.isArray(on) || !Array.isArray(off) || on.length !== 2 || off.length !== 2) return null;
  if (on[0] !== "on" || off[0] !== "off" || !Number.isInteger(on[1]) || !Number.isInteger(off[1])) return null;
  return on[1] >= 0 && on[1] <= 100 && on[1] + off[1] === 100 ? on[1] : null;
}

/** "targeting added/removed/changed", or "rollout N% → M%" between two simple rollouts; null when unchanged. */
function describeTargetingChange(before, after) {
  const was = ruleOf(before), now = ruleOf(after);
  if (sameRule(was, now)) return null;
  if (was === null) return "targeting added";
  if (now === null) return "targeting removed";
  const from = rolloutPercentage(was), to = rolloutPercentage(now);
  return from !== null && to !== null ? `rollout ${from}% → ${to}%` : "targeting changed";
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
      submit.disabled = true;
      try {
        const created = await api.createFlag({
          key: key.value,
          enabled: enabled.checked,
          description: description.value.trim() || undefined,
          tags: parseTags(tags.value),
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
  h("label", { class: "inline" }, enabled, "Enabled"),
  h("label", {}, "Description", description),
  h("label", {}, "Tags", tags),
  h("div", { class: "actions" }, submit, h("a", { href: "#/", class: "button" }, "Cancel")));

  setTimeout(() => key.focus());
  return [h("p", {}, h("a", { href: "#/" }, "← Flags")), h("h1", {}, "New flag"), form];
}

// ---------------------------------------------------------------- router

let generation = 0;

async function route() {
  const current = ++generation;
  const hash = location.hash.replace(/^#/, "") || "/";
  let content;
  let section = "list";
  if (hash === "/new") {
    section = "new";
    content = await renderCreate();
  } else if (hash.startsWith("/flags/")) {
    content = await renderDetail(decodeURIComponent(hash.slice("/flags/".length)));
  } else {
    content = await renderList();
  }
  if (current !== generation) return; // a newer navigation won
  for (const link of document.querySelectorAll("[data-nav]")) {
    link.toggleAttribute("aria-current", link.dataset.nav === section);
  }
  view.replaceChildren(...content);
}

window.addEventListener("hashchange", () => {
  if (!notice.classList.contains("success")) showNotice("");
  route();
});
route();
