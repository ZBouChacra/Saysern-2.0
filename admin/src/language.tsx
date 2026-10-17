import { Fragment, createContext, useContext, useMemo, useState, useEffect, type ReactNode } from "react";
import { setTexts, t } from "./texts";

interface Language { code: string; dir: "ltr" | "rtl"; name: string }
interface LanguageState { lang: string; setLang: (code: string) => void; languages: Language[] }
const Ctx = createContext<LanguageState | null>(null);
const KEY = "sayserne.admin.lang";

/** The back office follows the language chosen in its header (remembered in this browser), else the site's default. Texts are loaded before anything is drawn. */
export function LanguageProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{ lang: string; languages: Language[] } | null>(null);
  const [failed, setFailed] = useState(false);

  const apply = (lang: string, languages: Language[]) => { document.documentElement.lang = lang; document.documentElement.dir = languages.find(l => l.code === lang)?.dir ?? "ltr"; };
  const load = (lang: string) => fetch(`/api/translations/${lang}`).then(r => (r.ok ? r.json() : {})).catch(() => ({}));

  useEffect(() => {
    (async () => {
      const [config, values] = await Promise.all([fetch("/api/config/public").then(r => r.json()), fetch("/api/lookups/language").then(r => r.json())]);
      const languages: Language[] = (values as { code: string; metadata?: { dir?: string }; names: Record<string, string> }[])
        .map(v => ({ code: v.code, dir: v.metadata?.dir === "rtl" ? "rtl" as const : "ltr" as const, name: v.names[v.code] ?? v.names.en ?? v.code }));
      let saved: string | null = null;
      try { saved = localStorage.getItem(KEY); } catch { /* storage unavailable */ }
      const lang = languages.some(l => l.code === saved) ? saved! : String(config["Site.DefaultLanguage"] ?? "en");
      setTexts(await load(lang)); apply(lang, languages);
      setState({ lang, languages });
    })().catch(() => setFailed(true));
  }, []);

  const value = useMemo<LanguageState | null>(() => state && ({
    ...state,
    setLang: (code: string) => { void load(code).then(texts => { setTexts(texts); apply(code, state.languages); setState({ ...state, lang: code }); try { localStorage.setItem(KEY, code); } catch { /* ignore */ } }); },
  }), [state]);

  if (failed) return <main className="card"><h1>{t("Cannot reach the server")}</h1><p className="muted">{t("Check the connection and reload the page.")}</p></main>;
  if (!value) return <p className="muted" style={{ padding: "2rem" }}>{t("Loading…")}</p>;
  return <Ctx.Provider value={value}><Fragment key={value.lang}>{children}</Fragment></Ctx.Provider>;
}

export function useLanguage(): LanguageState { const v = useContext(Ctx); if (!v) throw new Error("LanguageProvider is missing"); return v; }
