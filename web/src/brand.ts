// The site's colours come from Settings (Brand.*). The defaults are the stylesheet's own, so nothing is overridden until an admin picks colours.
export const BRAND_DEFAULTS = { primary: "#2d6d5f", dark: "#1f4d43", accent: "#d4a757" };

const toRgb = (hex: string): [number, number, number] => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)) as [number, number, number];
const toHex = (rgb: number[]) => "#" + rgb.map(v => Math.round(Math.min(255, Math.max(0, v))).toString(16).padStart(2, "0")).join("");

/** `amount` of `other` mixed into `hex` (0 = unchanged, 1 = other). Used to lighten a colour for dark mode. */
export function mix(hex: string, other: string, amount: number): string {
  const a = toRgb(hex), b = toRgb(other);
  return toHex(a.map((v, i) => v + (b[i] - v) * amount));
}

const channel = (v: number) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
export const luminance = (hex: string) => { const [r, g, b] = toRgb(hex); return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b); };
/** WCAG contrast ratio of white text on this colour (4.5 or more is comfortable to read). */
export const contrastWithWhite = (hex: string) => 1.05 / (luminance(hex) + 0.05);

export function applyBrand(colours: { primary: string; dark: string; accent: string }) {
  const root = document.documentElement;
  const valid = (c: string) => /^#[0-9a-fA-F]{6}$/.test(c);
  const isDark = () => root.getAttribute("data-theme") === "dark" || (root.getAttribute("data-theme") !== "light" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  const apply = () => {
    const { primary, dark, accent } = colours;
    const untouched = primary.toLowerCase() === BRAND_DEFAULTS.primary && dark.toLowerCase() === BRAND_DEFAULTS.dark && accent.toLowerCase() === BRAND_DEFAULTS.accent;
    if (untouched || !valid(primary) || !valid(dark) || !valid(accent)) { for (const v of ["--green", "--green-dark", "--gold"]) root.style.removeProperty(v); return; }
    const night = isDark();   // a dark page needs lighter shades of the brand colours to stay readable
    root.style.setProperty("--green", night ? mix(primary, "#ffffff", 0.3) : primary);
    root.style.setProperty("--green-dark", night ? mix(dark, "#ffffff", 0.2) : dark);
    root.style.setProperty("--gold", accent);
  };
  apply();
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", apply);
  new MutationObserver(apply).observe(root, { attributes: true, attributeFilter: ["data-theme"] });
}
