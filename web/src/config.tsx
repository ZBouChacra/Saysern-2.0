import { Fragment, createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { setTexts } from "./texts";

/** Everything the site needs to render itself comes from the API: public settings, languages, lookups, texts. Nothing is compiled in. */

export interface LookupValue { code: string; parentCode?: string; sortOrder: number; metadata?: Record<string, unknown>; names: Record<string, string> }
export type PublicConfig = Record<string, string | number | boolean>;

const lookupCache = new Map<string, Promise<LookupValue[]>>();
export function loadLookup(type: string, parent?: string): Promise<LookupValue[]> {
  const key = `${type}|${parent ?? ""}`;
  let p = lookupCache.get(key);
  if (!p) {
    p = fetch(`/api/lookups/${type}${parent ? `?parent=${encodeURIComponent(parent)}` : ""}`).then(r => (r.ok ? (r.json() as Promise<LookupValue[]>) : Promise.reject(new Error(`lookup ${type}: ${r.status}`))));
    p.catch(() => lookupCache.delete(key));   // a failed load may be retried
    lookupCache.set(key, p);
  }
  return p;
}
export const clearLookupCache = () => lookupCache.clear();

interface ConfigState { config: PublicConfig; lang: string; setLang: (l: string) => void; languages: LookupValue[] }
const Ctx = createContext<ConfigState | null>(null);
const LANG_KEY = "sayserne.lang";
function applyDirection(lang: string, languages: LookupValue[]) {
  document.documentElement.lang = lang;
  document.documentElement.dir = languages.find(l => l.code === lang)?.metadata?.dir === "rtl" ? "rtl" : "ltr";
}

export function ConfigProvider({ children }: { children: ReactNode }) {
  const [config, setConfig] = useState<PublicConfig | null>(null);
  const [languages, setLanguages] = useState<LookupValue[]>([]);
  const [lang, setLangState] = useState<string>("");
  const [failed, setFailed] = useState(false);

  const loadTexts = (language: string) => fetch(`/api/translations/${language}`).then(r => (r.ok ? r.json() : {})).catch(() => ({}));

  // Texts are loaded together with the config and BEFORE anything renders, so messages worked out on first render (e.g. "?error=login_required") are in the right language.
  useEffect(() => {
    (async () => {
      const [cfg, langs] = await Promise.all([fetch("/api/config/public").then(r => (r.ok ? r.json() : Promise.reject(new Error("config")))), loadLookup("language")]) as [PublicConfig, LookupValue[]];
      let saved: string | null = null;
      try { saved = localStorage.getItem(LANG_KEY); } catch { /* storage unavailable */ }
      const chosen = langs.some(l => l.code === saved) ? saved! : String(cfg["Site.DefaultLanguage"] ?? "en");
      setTexts(await loadTexts(chosen));
      applyDirection(chosen, langs);
      setConfig(cfg); setLanguages(langs); setLangState(chosen);
    })().catch(() => setFailed(true));
  }, []);

  const value = useMemo<ConfigState | null>(() => config && lang ? ({
    config, lang, languages,
    // load the new language's texts first, then switch; the tree is re-created (key) so nothing keeps text from the old language
    setLang: (l: string) => { void loadTexts(l).then(t => { setTexts(t); applyDirection(l, languages); setLangState(l); try { localStorage.setItem(LANG_KEY, l); } catch { /* ignore */ } }); },
  }) : null, [config, lang, languages]);

  if (failed) return <main className="card"><h1>We can't reach the server</h1><p className="muted">Please check your connection and reload the page.</p></main>;
  if (!value) return <p className="muted" style={{ padding: "2rem" }}>Loading…</p>;
  return <Ctx.Provider value={value}><Fragment key={lang}>{children}</Fragment></Ctx.Provider>;
}

function useState_() { const v = useContext(Ctx); if (!v) throw new Error("ConfigProvider is missing"); return v; }

export function useConfig() {
  const { config } = useState_();
  return useMemo(() => ({
    num: (k: string) => Number(config[k]),
    bool: (k: string) => config[k] === true,
    str: (k: string) => String(config[k] ?? ""),
  }), [config]);
}
export const useLang = () => { const { lang, setLang, languages } = useState_(); return { lang, setLang, languages }; };

/** The (active) values of a lookup, with a helper that names a code in the current language (falling back to English, then the code). */
export function useLookup(type: string, parent?: string) {
  const { lang } = useState_();
  const [values, setValues] = useState<LookupValue[]>([]);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    let live = true;
    setLoading(true);
    loadLookup(type, parent).then(v => { if (live) { setValues(v); setLoading(false); } }).catch(() => { if (live) { setValues([]); setLoading(false); } });
    return () => { live = false; };
  }, [type, parent]);
  const name = (v: LookupValue) => v.names[lang] ?? v.names.en ?? v.code;
  const label = (code?: string | null) => { if (!code) return ""; const v = values.find(x => x.code === code); return v ? name(v) : code; };
  return { values, loading, name, label };
}
