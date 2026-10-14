import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type Paged, type Role, type User } from "../api";
import { useAuth } from "../auth";

export default function Admin() {
  const { user: me } = useAuth();
  const [search, setSearch] = useState("");
  const [query, setQuery] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<User> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<number | null>(null);

  const load = useCallback(async () => {
    try {
      setData(await api.adminUsers(query, page));
      setError(null);
    } catch (e) {
      setError(errorMessage(e));
    }
  }, [query, page]);
  useEffect(() => { void load(); }, [load]);

  async function act(id: number, fn: () => Promise<User>) {
    setBusyId(id);
    try { await fn(); await load(); } catch (e) { setError(errorMessage(e)); } finally { setBusyId(null); }
  }

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <main className="card wide">
      <h1>Back office · Users</h1>
      <form className="row" onSubmit={e => { e.preventDefault(); setPage(1); setQuery(search); }}>
        <input type="search" placeholder="Search name or email" value={search} onChange={e => setSearch(e.target.value)} aria-label="Search users" />
        <button className="btn">Search</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (
        <>
          <div className="table-wrap">
            <table>
              <thead><tr><th>Name</th><th>Email</th><th>Role</th><th>Status</th><th>Created</th><th /></tr></thead>
              <tbody>
                {data.items.map(u => (
                  <tr key={u.id}>
                    <td>{u.firstName} {u.lastName}</td>
                    <td>{u.email}</td>
                    <td><span className={`pill ${u.role === "admin" ? "ok" : ""}`}>{u.role}</span></td>
                    <td>{u.isLocked ? <span className="pill bad">locked</span> : "active"}</td>
                    <td>{new Date(u.createdAtUtc).toLocaleDateString()}</td>
                    <td className="actions">
                      {u.id === me?.id ? <span className="muted">you</span> : (
                        <>
                          <button className="btn small secondary" disabled={busyId === u.id} onClick={() => act(u.id, () => api.setLocked(u.id, !u.isLocked))}>{u.isLocked ? "Unlock" : "Lock"}</button>
                          <button className="btn small secondary" disabled={busyId === u.id} onClick={() => act(u.id, () => api.setRole(u.id, (u.role === "admin" ? "user" : "admin") as Role))}>{u.role === "admin" ? "Make user" : "Make admin"}</button>
                        </>
                      )}
                    </td>
                  </tr>
                ))}
                {data.items.length === 0 && <tr><td colSpan={6} className="muted">No users found.</td></tr>}
              </tbody>
            </table>
          </div>
          <p className="row spread">
            <span className="muted">{data.total} user{data.total === 1 ? "" : "s"}</span>
            <span className="row">
              <button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
              <span>Page {page} of {pages}</span>
              <button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button>
            </span>
          </p>
        </>
      )}
    </main>
  );
}
