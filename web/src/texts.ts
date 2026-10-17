// UI texts served by the API (/api/translations/<language>) and kept in the database, so an admin can reword them.
// The only text compiled into the app is what must work when the API cannot be reached.
let texts: Record<string, string> = {};
export const setTexts = (t: Record<string, string>) => { texts = t; };
export const getText = (key: string): string | undefined => texts[key];
export const NETWORK_ERROR = "Network error. Please try again.";
export const GENERIC_ERROR = "Something went wrong. Please try again.";

/** Short phrases are their own key; a long one is cut and given a hash, so every key stays within the database's limit (db/tools/ui_keys.py does the same). */
export function uiKey(english: string): string {
  if (english.length <= 140) return "ui:" + english;
  let h = 0x811c9dc5;
  for (let i = 0; i < english.length; i++) { h ^= english.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return "ui:" + english.slice(0, 110) + "…#" + h.toString(16).padStart(8, "0");
}

/**
 * The text for an English phrase in the current language. The English phrase IS the key (stored as "ui:<phrase>" in Texts), so English works with
 * nothing loaded, and an admin can reword any phrase or translate it. {name} placeholders are filled from `vars`.
 */
export function t(english: string, vars?: Record<string, string | number>): string {
  const text = texts[uiKey(english)] ?? english;
  return vars ? text.replace(/\{(\w+)\}/g, (match, key: string) => (key in vars ? String(vars[key]) : match)) : text;
}
