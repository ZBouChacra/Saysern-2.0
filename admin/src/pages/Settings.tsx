import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type Setting } from "../api";
import { useSession } from "../session";

export default function Settings() {
  const { can } = useSession();
  const [rows, setRows] = useState<Setting[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [msg, setMsg] = useState<Record<string, { ok: boolean; text: string }>>({});
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { const r = await api.settings(); setRows(r); setDraft(Object.fromEntries(r.map(s => [s.key, s.value]))); } catch (e) { setError(errorMessage(e)); } }, []);
  useEffect(() => { void load(); }, [load]);
  const manage = can("settings.manage");
  const groups = [...new Set(rows.map(r => r.group))];

  async function save(s: Setting, reset = false) {
    try { const saved = reset ? await api.resetSetting(s.key) : await api.setSetting(s.key, draft[s.key]); setRows(rs => rs.map(r => (r.key === s.key ? saved : r))); setDraft(d => ({ ...d, [s.key]: saved.value }));
      setMsg(m => ({ ...m, [s.key]: { ok: true, text: reset ? "Reset to default." : "Saved." } })); }
    catch (e) { setMsg(m => ({ ...m, [s.key]: { ok: false, text: errorMessage(e) } })); }
  }

  return (
    <main className="card full">
      <h1>Settings</h1>
      <p className="muted">Business rules, editable at runtime. Each value has limits set by the system; security-sensitive ones have a floor you cannot go below. Changes apply immediately here and within 15 seconds on every other server, and are recorded in the audit log.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {groups.map(g => (
        <section key={g}><h2>{g}</h2>
          {rows.filter(r => r.group === g).map(s => (
            <div className="setting" key={s.key} data-setting={s.key}>
              <div><code>{s.key}</code>{s.isPublic && <span className="pill" style={{ marginLeft: ".4rem" }}>public</span>}<div className="meta">{s.description}</div></div>
              <div>
                {s.type === "bool" ? <select aria-label={s.key} disabled={!manage} value={draft[s.key] ?? ""} onChange={e => setDraft({ ...draft, [s.key]: e.target.value })}><option value="true">on</option><option value="false">off</option></select>
                  : <input aria-label={s.key} disabled={!manage} type={s.type === "string" ? "text" : "number"} value={draft[s.key] ?? ""} min={s.min} max={s.max} onChange={e => setDraft({ ...draft, [s.key]: e.target.value })} />}
                <div className="meta">default {s.default}{s.min !== undefined ? ` · allowed ${s.min}–${s.max}` : ""}</div>
                {msg[s.key] && <div className={msg[s.key].ok ? "ok-text small" : "error small"} role="status">{msg[s.key].text}</div>}
              </div>
              <div className="row">{manage && <><button className="btn small" disabled={draft[s.key] === s.value} onClick={() => void save(s)}>Save</button>
                <button className="btn small secondary" disabled={s.value === s.default} onClick={() => void save(s, true)}>Reset</button></>}</div>
            </div>
          ))}
        </section>
      ))}
    </main>
  );
}
