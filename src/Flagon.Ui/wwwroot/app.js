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
  );
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

const listState = { q: "", tag: "", state: "", source: "", archived: "active", hygiene: "" };

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

// ---------------------------------------------------------------- detail

async function renderDetail(key) {
  let flag, audit;
  try {
    [flag, audit] = await Promise.all([api.getFlag(key), api.audit(key)]);
  } catch (e) {
    return [h("p", {}, h("a", { href: "#/" }, "← Flags")), errorView(e)];
  }

  const reload = () => route();

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
          h("td", {}, describeChange(entry)))))));

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
  const raw = { before, after };
  return h("details", {},
    h("summary", {}, changes.join(", ") || "details"),
    h("pre", {}, JSON.stringify(raw, null, 2)));
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
