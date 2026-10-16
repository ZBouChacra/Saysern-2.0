import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AuditRow, type Paged } from "../api";

export default function Audit() {
  const [f, setF] = useState({ search: "", action: "", entityType: "", actorId: "", from: "", to: "" });
  const [applied, setApplied] = useState(f);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<AuditRow> | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { setData(await api.audit({ ...applied, from: applied.from ? new Date(applied.from).toISOString() : undefined, to: applied.to ? new Date(applied.to + "T23:59:59").toISOString() : undefined, page, pageSize: 25 })); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }, [applied, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });

  return (
    <main className="card full">
      <h1>Audit log</h1>
      <p className="muted">Every back-office change, written in the same database transaction as the change itself. Entries can't be edited or deleted.</p>
      <form className="toolbar" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(f); }}>
        <input placeholder="Search" aria-label="Search audit" value={f.search} onChange={set("search")} />
        <input placeholder="Action (e.g. settings.update)" aria-label="Action" value={f.action} onChange={set("action")} />
        <input placeholder="Entity (e.g. user)" aria-label="Entity type" value={f.entityType} onChange={set("entityType")} />
        <input placeholder="Actor id" aria-label="Actor id" value={f.actorId} onChange={set("actorId")} style={{ maxWidth: 110 }} />
        <input type="date" aria-label="From" value={f.from} onChange={set("from")} /><input type="date" aria-label="To" value={f.to} onChange={set("to")} />
        <button className="btn">Filter</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (
        <>
          <div className="table-wrap"><table className="t"><thead><tr><th>When</th><th>Who</th><th>Action</th><th>What</th><th /></tr></thead><tbody>
            {data.items.map(r => (
              <>
                <tr key={r.id}><td>{new Date(r.occurredAt).toLocaleString()}</td><td>{r.actorName ?? <span className="muted">system</span>}</td><td><code>{r.action}</code></td><td>{r.summary}</td>
                  <td>{r.details !== undefined && <button className="btn small secondary" onClick={() => setOpen(open === r.id ? null : r.id)}>{open === r.id ? "Hide" : "Details"}</button>}</td></tr>
                {open === r.id && <tr key={`${r.id}d`}><td colSpan={5}><pre className="json">{JSON.stringify(r.details, null, 2)}</pre>{r.ip && <span className="muted small">IP {r.ip}</span>}</td></tr>}
              </>
            ))}
            {data.items.length === 0 && <tr><td colSpan={5} className="muted">No entries.</td></tr>}
          </tbody></table></div>
          <p className="row spread"><span className="muted">{data.total} entr{data.total === 1 ? "y" : "ies"}</span>
            <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
        </>
      )}
    </main>
  );
}
