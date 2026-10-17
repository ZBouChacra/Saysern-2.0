// Colour helpers for the Brand page (the same maths the public site uses to keep text readable).
const toRgb = (hex: string): [number, number, number] => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)) as [number, number, number];
const channel = (v: number) => { const s = v / 255; return s <= 0.03928 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
export const luminance = (hex: string) => { const [r, g, b] = toRgb(hex); return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b); };
/** WCAG contrast ratio of white text on this colour (4.5 or more is comfortable to read). */
export const contrastWithWhite = (hex: string) => 1.05 / (luminance(hex) + 0.05);
export const isHex = (v: string) => /^#[0-9a-fA-F]{6}$/.test(v);
