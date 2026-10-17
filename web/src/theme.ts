export type ThemeChoice = "system" | "light" | "dark";
const KEY = "sayserne.theme";

export function getTheme(): ThemeChoice {
  try { const v = localStorage.getItem(KEY); return v === "light" || v === "dark" ? v : "system"; } catch { return "system"; }
}

/** "system" follows the device; "light" and "dark" force a look (a data attribute the stylesheet reacts to). */
export function applyTheme(choice: ThemeChoice) {
  const root = document.documentElement;
  if (choice === "system") root.removeAttribute("data-theme"); else root.setAttribute("data-theme", choice);
}

export function setTheme(choice: ThemeChoice) {
  try { if (choice === "system") localStorage.removeItem(KEY); else localStorage.setItem(KEY, choice); } catch { /* storage unavailable: it lasts for this visit */ }
  applyTheme(choice);
}
