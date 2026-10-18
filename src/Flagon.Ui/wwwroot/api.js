// Thin client for the Flagon management API, which MapFlagonUi mounts at "./_api" next to this page.

const base = new URL("_api/", document.baseURI);

/** An error response from the management API (problem details in `problem`). */
export class ApiError extends Error {
  constructor(status, problem) {
    super(problem?.detail || problem?.title || `Request failed (${status})`);
    this.status = status;
    this.problem = problem ?? {};
  }

  /** True for a stale expected version (409 carrying currentVersion), as opposed to e.g. "already exists". */
  get isVersionConflict() {
    return this.status === 409 && this.problem.currentVersion !== undefined;
  }
}

async function request(method, path, { body, version } = {}) {
  const headers = { Accept: "application/json" };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  if (version !== undefined) headers["If-Match"] = `"${version}"`;

  let response;
  try {
    response = await fetch(new URL(path, base), {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      credentials: "same-origin",
      cache: "no-store",
    });
  } catch (e) {
    throw new ApiError(0, { title: "Network error", detail: `Could not reach the management API: ${e.message}` });
  }

  if (response.status === 204) return null;
  const text = await response.text();
  let payload = null;
  if (text) {
    try { payload = JSON.parse(text); } catch { payload = { detail: text }; }
  }
  if (!response.ok) {
    if (response.status === 401 || response.status === 403) {
      payload = { title: "Not allowed", detail: "You are not authorised to manage flags.", ...payload };
    }
    throw new ApiError(response.status, payload);
  }
  return payload;
}

const flag = (key) => `flags/${encodeURIComponent(key)}`;

export const api = {
  listFlags: () => request("GET", "flags"),
  getFlag: (key) => request("GET", flag(key)),
  /** A boolean flag from `enabled`, or (with `variants`, name → JSON value) a string, number or object flag. */
  createFlag: ({ key, enabled, description, tags, variants, defaultVariant }) =>
    request("POST", "flags", { body: { key, enabled, description, tags, variants, defaultVariant } }),
  updateMetadata: (key, version, { description, tags }) => request("PATCH", flag(key), { body: { description, tags }, version }),
  setEnabled: (key, version, enabled) => request("PUT", `${flag(key)}/enabled`, { body: { enabled }, version }),
  setTargeting: (key, version, targeting) => request("PUT", `${flag(key)}/targeting`, { body: { targeting }, version }),
  clearTargeting: (key, version) => request("DELETE", `${flag(key)}/targeting`, { version }),
  /** Previews the stored flag, or the unsaved flagd `document` when given, for `context`. No side effects. */
  evaluate: (key, context = {}, document) =>
    request("POST", `${flag(key)}/evaluate`, { body: document === undefined ? { context } : { context, document } }),
  archive: (key, version) => request("POST", `${flag(key)}/archive`, { version }),
  restore: (key, version) => request("POST", `${flag(key)}/restore`, { version }),
  purge: (key, version) => request("DELETE", flag(key), { version }),
  audit: (key) => request("GET", `${flag(key)}/audit`),
};
