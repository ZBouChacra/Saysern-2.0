/** Latin digits for Arabic too, so dates, amounts and phone numbers read the same in both languages. */
const locale = () => { const lang = document.documentElement.lang || "en"; return lang === "ar" ? "ar-u-nu-latn" : lang; };
export const formatWhen = (iso: string) => new Date(iso).toLocaleString(locale(), { dateStyle: "medium", timeStyle: "short" });
export const formatDay = (iso: string) => new Date(iso).toLocaleDateString(locale(), { dateStyle: "medium" });
