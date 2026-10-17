// Times are shown in the signed-in person's own time zone (Settings), otherwise in the browser's.
let zone: string | undefined;
export const setTimeZone = (tz?: string | null) => { zone = tz || undefined; };

/** Latin digits for Arabic too: phone numbers, prices and dates then read the same in both languages. */
const locale = () => { const lang = document.documentElement.lang || "en"; return lang === "ar" ? "ar-u-nu-latn" : lang; };

export function formatWhen(iso: string): string {
  const options: Intl.DateTimeFormatOptions = { dateStyle: "medium", timeStyle: "short" };
  try { return new Date(iso).toLocaleString(locale(), { ...options, timeZone: zone }); }
  catch { return new Date(iso).toLocaleString(locale(), options); }   // an unknown zone name must never break a page
}
