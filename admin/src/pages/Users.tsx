import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AdminUser, type Paged, type Role } from "../api";
import { useSession } from "../session";

export default function Users() {
  const { can, session } = useSession();
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<AdminUser> | null>(null);
  const [roles, setRoles] = useState<Role[]>([]);
  const [editing, setEditing] = useState<AdminUser | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(async () => { try { setData(await api.users(query, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [query, page]);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (can("roles.manage")) api.roles().then(setRoles).catch(() => undefined); }, [can]);

  async function act(fn: () => Promise<unknown>, done?: string) {
    try { await fn(); setNotice(done ?? null); await load(); } catch (e) { setError(errorMessage(e)); }
  }
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <main className="card full">
      <h1>Users</h1>
      <form className="toolbar" onSubmit={e => { e.preventDefault(); setPage(1); setQuery(search); }}>
        <input type="search" placeholder="Search name or email" aria-label="Search users" value={search} onChange={e => setSearch(e.target.value)} /><button className="btn">Search</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {notice && <p className="ok-text" role="status">{notice}</p>}
      {data && (
        <>
          <div className="table-wrap"><table className="t">
            <thead><tr><th>Name</th><th>Email</th><th>Roles</th><th>2FA</th><th>Status</th><th>Listing</th><th /></tr></thead>
            <tbody>
              {data.items.map(u => (
                <tr key={u.id}>
                  <td>{u.firstName} {u.lastName}</td><td>{u.email}</td>
                  <td>{u.role === "admin" && <span className="pill ok">super admin</span>} {u.roleNames}</td>
                  <td>{u.hasTwoFactor ? "on" : "—"}</td>
                  <td>{u.isLocked ? <span className="pill bad">locked</span> : "active"}</td>
                  <td>{u.isPremium && <span className="pill ok">premium</span>} {u.isStarred && <span className="pill">★</span>}</td>
                  <td className="actions">
                    {u.id === session?.user.id ? <span className="muted">you</span> : (
                      <>
                        {can("users.manage") && <button className="btn small secondary" onClick={() => void act(() => api.lockUser(u.id, !u.isLocked), u.isLocked ? "Unlocked." : "Locked.")}>{u.isLocked ? "Unlock" : "Lock"}</button>}
                        {can("users.manage") && <button className="btn small secondary" onClick={() => void act(() => api.setFlags(u.id, !u.isPremium, u.isStarred))}>{u.isPremium ? "Unpremium" : "Premium"}</button>}
                        {can("users.manage") && <button className="btn small secondary" onClick={() => void act(() => api.setFlags(u.id, u.isPremium, !u.isStarred))}>{u.isStarred ? "Unstar" : "Star"}</button>}
                        {can("roles.manage") && <button className="btn small secondary" onClick={() => setEditing(u)}>Roles…</button>}
                        {can("admins.manage") && <button className="btn small secondary" onClick={() => void act(() => api.setSuperAdmin(u.id, u.role === "admin" ? "user" : "admin"))}>{u.role === "admin" ? "Remove super admin" : "Make super admin"}</button>}
                        {can("admins.manage") && u.hasTwoFactor && <button className="btn small secondary" onClick={() => { if (window.confirm(`Reset two-factor for ${u.email}? They will enroll again at their next sign-in.`)) void act(() => api.resetTwoFactor(u.id), "Two-factor reset.") }}>Reset 2FA</button>}
                      </>
                    )}
                  </td>
                </tr>
              ))}
              {data.items.length === 0 && <tr><td colSpan={7} className="muted">No users found.</td></tr>}
            </tbody>
          </table></div>
          <p className="row spread"><span className="muted">{data.total} user{data.total === 1 ? "" : "s"}</span>
            <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
        </>
      )}
      {editing && <RolesDialog user={editing} roles={roles} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); void load(); }} />}
    </main>
  );
}

function RolesDialog({ user, roles, onClose, onSaved }: { user: AdminUser; roles: Role[]; onClose: () => void; onSaved: () => void }) {
  const have = new Set((user.roleNames ?? "").split(", ").filter(Boolean));
  const [chosen, setChosen] = useState<Set<number>>(new Set(roles.filter(r => have.has(r.name)).map(r => r.id)));
  const [error, setError] = useState<string | null>(null);
  const toggle = (id: number) => setChosen(s => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });
  async function save() { try { await api.setUserRoles(user.id, [...chosen]); onSaved(); } catch (e) { setError(errorMessage(e)); } }
  return (
    <div className="modal" role="dialog" aria-label="Roles"><div>
      <h2>Roles for {user.firstName} {user.lastName}</h2>
      <p className="muted small">Staff can sign in to the back office once they hold at least one role (or are a super admin).</p>
      {roles.map(r => <label className="chk" key={r.id}><input type="checkbox" checked={chosen.has(r.id)} onChange={() => toggle(r.id)} /><span><b>{r.name}</b><br /><span className="muted small">{r.permissions.join(", ") || "no permissions"}</span></span></label>)}
      {error && <p className="error" role="alert">{error}</p>}
      <div className="row"><button className="btn" onClick={() => void save()}>Save roles</button><button className="btn secondary" onClick={onClose}>Cancel</button></div>
    </div></div>
  );
}
