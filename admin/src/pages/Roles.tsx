import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type Permission, type Role } from "../api";

import { t } from "../texts";
export default function Roles() {
  const [roles, setRoles] = useState<Role[]>([]);
  const [perms, setPerms] = useState<Permission[]>([]);
  const [editing, setEditing] = useState<{ id: number | null; name: string; description: string; permissions: Set<string> } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { const [r, p] = await Promise.all([api.roles(), api.permissions()]); setRoles(r); setPerms(p); } catch (e) { setError(errorMessage(e)); } }, []);
  useEffect(() => { void load(); }, [load]);
  const groups = [...new Set(perms.map(p => p.group))];

  async function save() {
    if (!editing) return;
    try { await api.saveRole(editing.id, { name: editing.name, description: editing.description || undefined, permissions: [...editing.permissions] }); setEditing(null); setError(null); await load(); }
    catch (e) { setError(errorMessage(e)); }
  }
  async function remove(r: Role) {
    if (!window.confirm(t("Delete the role “{name}”?", { name: r.name }))) return;
    try { await api.deleteRole(r.id); await load(); } catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="card full">
      <div className="row spread"><h1>{t("Roles")}</h1><button className="btn" onClick={() => setEditing({ id: null, name: "", description: "", permissions: new Set() })}>{t("New role")}</button></div>
      <p className="muted">{t("A role is a named set of permissions. Permissions are fixed by the system; roles are yours to define. Super admins always hold every permission.")}</p>
      {error && !editing && <p className="error" role="alert">{error}</p>}
      <div className="table-wrap"><table className="t"><thead><tr><th>{t("Role")}</th><th>{t("Permissions")}</th><th>{t("Users")}</th><th /></tr></thead><tbody>
        {roles.map(r => (
          <tr key={r.id}><td><b>{r.name}</b><div className="muted small">{r.description}</div></td><td>{r.permissions.join(", ") || "—"}</td><td>{r.userCount}</td>
            <td className="actions"><button className="btn small secondary" onClick={() => setEditing({ id: r.id, name: r.name, description: r.description ?? "", permissions: new Set(r.permissions) })}>{t("Edit")}</button>
              <button className="btn small secondary" onClick={() => void remove(r)}>{t("Delete")}</button></td></tr>
        ))}
      </tbody></table></div>
      {editing && (
        <div className="modal" role="dialog" aria-label={t("Role")}><div>
          <h2>{editing.id === null ? t("New role") : t("Edit role")}</h2>
          <label>{t("Name")}<input value={editing.name} onChange={e => setEditing({ ...editing, name: e.target.value })} /></label>
          <label>{t("Description")}<input value={editing.description} onChange={e => setEditing({ ...editing, description: e.target.value })} /></label>
          {groups.map(g => (
            <div className="perm-group" key={g}><h4>{g}</h4>
              {perms.filter(p => p.group === g).map(p => (
                <label className="chk" key={p.code}><input type="checkbox" checked={editing.permissions.has(p.code)} onChange={() => { const s = new Set(editing.permissions); s.has(p.code) ? s.delete(p.code) : s.add(p.code); setEditing({ ...editing, permissions: s }); }} />
                  <span><code>{p.code}</code><br /><span className="muted small">{p.description}</span></span></label>
              ))}
            </div>
          ))}
          {error && <p className="error" role="alert">{error}</p>}
          <div className="row"><button className="btn" onClick={() => void save()}>{t("Save")}</button><button className="btn secondary" onClick={() => { setEditing(null); setError(null); }}>{t("Cancel")}</button></div>
        </div></div>
      )}
    </main>
  );
}
