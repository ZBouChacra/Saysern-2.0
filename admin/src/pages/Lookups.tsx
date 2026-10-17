import { useCallback, useEffect, useMemo, useState } from "react";
import { api, errorMessage, type LookupType, type LookupValue } from "../api";

import { t } from "../texts";
interface Draft { code: string; isNew: boolean; parentCode: string; sortOrder: string; isActive: boolean; metadata: string; names: Record<string, string> }

export default function Lookups() {
  const [types, setTypes] = useState<LookupType[]>([]);
  const [type, setType] = useState<LookupType | null>(null);
  const [parents, setParents] = useState<LookupValue[]>([]);
  const [parent, setParent] = useState("");
  const [values, setValues] = useState<LookupValue[]>([]);
  const [languages, setLanguages] = useState<LookupValue[]>([]);
  const [filter, setFilter] = useState("");
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadTypes = useCallback(async () => { try { setTypes(await api.lookupTypes()); setLanguages(await api.lookupValues("language")); } catch (e) { setError(errorMessage(e)); } }, []);
  useEffect(() => { void loadTypes(); }, [loadTypes]);
  useEffect(() => {
    setValues([]); setParent(""); setParents([]);
    if (!type) return;
    if (type.parentType) api.lookupValues(type.parentType).then(setParents).catch(e => setError(errorMessage(e)));
    else api.lookupValues(type.code).then(setValues).catch(e => setError(errorMessage(e)));
  }, [type]);
  useEffect(() => { if (type?.parentType && parent) api.lookupValues(type.code, parent).then(setValues).catch(e => setError(errorMessage(e))); }, [type, parent]);

  const lang = (v: LookupValue) => v.names[document.documentElement.lang] ?? v.names.en ?? v.code;
  const shown = useMemo(() => values.filter(v => !filter || `${v.code} ${Object.values(v.names).join(" ")}`.toLowerCase().includes(filter.toLowerCase())), [values, filter]);

  function edit(v?: LookupValue) {
    setDraft(v ? { code: v.code, isNew: false, parentCode: v.parentCode ?? parent, sortOrder: String(v.sortOrder), isActive: v.isActive, metadata: v.metadata ? JSON.stringify(v.metadata, null, 2) : "", names: { ...v.names } }
      : { code: "", isNew: true, parentCode: parent, sortOrder: String((values.at(-1)?.sortOrder ?? 0) + 1), isActive: true, metadata: "", names: {} });
    setError(null);
  }
  async function save() {
    if (!type || !draft) return;
    let metadata: unknown;
    if (draft.metadata.trim()) { try { metadata = JSON.parse(draft.metadata); } catch { setError(t("Metadata must be valid JSON.")); return; } }
    const names = Object.fromEntries(Object.entries(draft.names).filter(([, n]) => n.trim()));
    try {
      const saved = await api.saveLookupValue(type.code, draft.code.trim(), { parentCode: type.parentType ? draft.parentCode : undefined, sortOrder: Number(draft.sortOrder) || 0, isActive: draft.isActive, metadata, names });
      setValues(vs => (vs.some(v => v.code === saved.code) ? vs.map(v => (v.code === saved.code ? saved : v)) : [...vs, saved]));
      setDraft(null); await loadTypes();
    } catch (e) { setError(errorMessage(e)); }
  }
  async function newType() {
    const code = window.prompt("Code for the new lookup (lowercase letters, digits, underscores)"); if (!code) return;
    const name = window.prompt("Display name"); if (!name) return;
    try { await api.saveLookupType(code, { name }); await loadTypes(); } catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="card full">
      <div className="row spread"><h1>{t("Lookups")}</h1><button className="btn secondary" onClick={() => void newType()}>{t("New lookup type")}</button></div>
      <p className="muted">{t("Every list the site offers (countries, cities, nationalities, durations, statuses…). Values are never deleted, only switched off, so existing data keeps its meaning.")}</p>
      {error && !draft && <p className="error" role="alert">{error}</p>}
      <div className="two">
        <nav aria-label={t("Lookup types")}>{types.map(t => (
          <button key={t.code} className={`room ${type?.code === t.code ? "active" : ""}`} onClick={() => setType(t)}><b>{t.name}</b> <span className="muted small">({t.valueCount})</span></button>
        ))}</nav>
        <section>
          {!type ? <p className="muted">{t("Choose a lookup.")}</p> : (
            <>
              <h2>{type.name}</h2><p className="muted small">{type.description}</p>
              <div className="toolbar">
                {type.parentType && <select aria-label={t("Parent")} value={parent} onChange={e => setParent(e.target.value)}><option value="">{t("Choose {parentType}…", { parentType: type.parentType })}</option>{parents.map(p => <option key={p.code} value={p.code}>{lang(p)}</option>)}</select>}
                <input placeholder={t("Filter")} aria-label={t("Filter values")} value={filter} onChange={e => setFilter(e.target.value)} />
                <button className="btn" disabled={!!type.parentType && !parent} onClick={() => edit()}>{t("Add value")}</button>
              </div>
              {type.parentType && !parent ? <p className="muted">{t("Choose a {parentType} to see and add its values.", { parentType: type.parentType })}</p> : (
                <div className="table-wrap"><table className="t"><thead><tr><th>{t("Code")}</th>{languages.map(l => <th key={l.code}>{l.names.en ?? l.code}</th>)}<th>{t("Order")}</th><th>{t("Active")}</th><th /></tr></thead><tbody>
                  {shown.map(v => (
                    <tr key={v.code}><td><code>{v.code}</code></td>{languages.map(l => <td key={l.code} dir={l.code === "ar" ? "rtl" : "ltr"}>{v.names[l.code] ?? <span className="muted">—</span>}</td>)}
                      <td>{v.sortOrder}</td><td>{v.isActive ? "yes" : <span className="pill bad">{t("off")}</span>}</td><td><button className="btn small secondary" onClick={() => edit(v)}>{t("Edit")}</button></td></tr>
                  ))}
                  {shown.length === 0 && <tr><td colSpan={languages.length + 4} className="muted">{t("No values.")}</td></tr>}
                </tbody></table></div>
              )}
            </>
          )}
        </section>
      </div>
      {draft && type && (
        <div className="modal" role="dialog" aria-label={t("Lookup value")}><div>
          <h2>{draft.isNew ? t("Add") : t("Edit")} {type.name.toLowerCase()}</h2>
          <label>{t("Code")}<input value={draft.code} disabled={!draft.isNew} onChange={e => setDraft({ ...draft, code: e.target.value })} /></label>
          {languages.map(l => <label key={l.code}>{t("Name ({language})", { language: l.names.en ?? l.code })}{l.code === "en" ? " *" : ""}<input dir={l.code === "ar" ? "rtl" : "ltr"} value={draft.names[l.code] ?? ""} onChange={e => setDraft({ ...draft, names: { ...draft.names, [l.code]: e.target.value } })} /></label>)}
          <div className="grid2"><label>{t("Order")}<input type="number" value={draft.sortOrder} onChange={e => setDraft({ ...draft, sortOrder: e.target.value })} /></label>
            <label className="chk"><input type="checkbox" checked={draft.isActive} onChange={e => setDraft({ ...draft, isActive: e.target.checked })} />{" "}{t("Active (offered to visitors)")}</label></div>
          <label>{t("Metadata (JSON, optional)")}<textarea rows={3} value={draft.metadata} onChange={e => setDraft({ ...draft, metadata: e.target.value })} /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="row"><button className="btn" onClick={() => void save()}>{t("Save")}</button><button className="btn secondary" onClick={() => setDraft(null)}>{t("Cancel")}</button></div>
        </div></div>
      )}
    </main>
  );
}
