import { useCallback, useEffect, useMemo, useState } from "react";
import { api, errorMessage, type TranslationRow } from "../api";

import { t } from "../texts";
export default function Translations() {
  const [rows, setRows] = useState<TranslationRow[]>([]);
  const [languages, setLanguages] = useState<string[]>([]);
  const [filter, setFilter] = useState("");
  const [edits, setEdits] = useState<Record<string, string>>({});
  const [msg, setMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { const [r, l] = await Promise.all([api.allTranslations(), api.lookupValues("language")]); setRows(r); setLanguages(l.map(x => x.code)); setEdits({}); } catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const byKey = useMemo(() => { const m = new Map<string, Map<string, TranslationRow>>(); for (const r of rows) { if (!m.has(r.key)) m.set(r.key, new Map()); m.get(r.key)!.set(r.language, r); } return m; }, [rows]);
  const keys = useMemo(() => [...byKey.keys()].filter(k => !filter || k.toLowerCase().includes(filter.toLowerCase()) || [...byKey.get(k)!.values()].some(r => r.value.toLowerCase().includes(filter.toLowerCase()))).sort(), [byKey, filter]);

  async function save(language: string, key: string, reset: boolean) {
    const id = `${language}|${key}`;
    try { const saved = reset ? await api.resetTranslation(language, key) : await api.saveTranslation(language, key, edits[id]);
      setRows(rs => [...rs.filter(r => !(r.language === language && r.key === key)), saved]); setEdits(e => { const n = { ...e }; delete n[id]; return n; }); setMsg(`${key} (${language}): ${reset ? "reset" : "saved"}.`); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="card full">
      <h1>{t("Texts")}</h1>
      <p className="muted">{t("Every text the site shows, per language. A language with no text for a key falls back to the default language. Editing keeps the original so you can reset it.")}</p>
      <div className="toolbar"><input placeholder={t("Search key or text")} aria-label={t("Search texts")} value={filter} onChange={e => setFilter(e.target.value)} /><span className="muted">{t("{length} of {size}", { length: keys.length, size: byKey.size })}</span></div>
      {error && <p className="error" role="alert">{error}</p>}{msg && <p className="ok-text" role="status">{msg}</p>}
      <div className="table-wrap"><table className="t"><thead><tr><th>{t("Key")}</th>{languages.map(l => <th key={l}>{l}</th>)}</tr></thead><tbody>
        {keys.map(k => (
          <tr key={k}><td><code>{k}</code></td>
            {languages.map(l => { const row = byKey.get(k)?.get(l); const id = `${l}|${k}`; const val = edits[id] ?? row?.value ?? "";
              return (<td key={l}>
                <textarea rows={Math.min(Math.max(2, val.split("\n").length + 1), 18)} dir={l === "ar" ? "rtl" : "ltr"} aria-label={`${k} ${l}`} value={val} onChange={e => setEdits({ ...edits, [id]: e.target.value })} />
                <div className="row">{edits[id] !== undefined && edits[id] !== row?.value && <button className="btn small" onClick={() => void save(l, k, false)}>{t("Save")}</button>}
                  {row && row.value !== row.defaultValue && <button className="btn small secondary" onClick={() => void save(l, k, true)}>{t("Reset")}</button>}</div>
              </td>); })}
          </tr>
        ))}
      </tbody></table></div>
    </main>
  );
}
