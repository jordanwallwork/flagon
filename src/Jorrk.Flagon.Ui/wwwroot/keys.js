// Flag key rules, mirroring Flagon's FlagKey: 1-128 characters, letters, digits, '.', '_' and '-',
// starting with a letter or digit. Keys are case-sensitive.

const keyPattern = /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$/;

/** Returns a message describing why `key` is not a valid flag key, or null when it is valid. */
export function keyProblem(key) {
  if (!key) return "A key is required.";
  if (key.length > 128) return "Keys are at most 128 characters.";
  if (!/^[a-zA-Z0-9]/.test(key)) return "Keys must start with a letter or digit.";
  if (!keyPattern.test(key)) return "Keys may only contain letters, digits, '.', '_' and '-'.";
  return null;
}

/** The form used to spot near-duplicates: case and separators ('.', '_', '-') are ignored. */
export function normalizeKey(key) {
  return key.toLowerCase().replace(/[._-]/g, "");
}

/** Existing keys that differ from `key` only by case or separators (an exact match is not a near-duplicate). */
export function nearDuplicates(key, existingKeys) {
  const normalized = normalizeKey(key);
  if (!normalized) return [];
  return existingKeys.filter((k) => k !== key && normalizeKey(k) === normalized);
}
