import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type JobRow, type JobStat, type Paged } from "../api";
import { useSession } from "../session";

const STATUSES = ["", "pending", "running", "done", "failed"];

export default function Jobs() {
  const { can } = useSession();
  const [status, setStatus] = useState("");
  const [type, setType] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<JobRow> | null>(null);
  const [stats, setStats] = useState<JobStat[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try { const [d, s] = await Promise.all([api.jobs(status, type, page), api.jobStats()]); setData(d); setStats(s); setError(null); } catch (e) { setError(errorMessage(e)); }
  }, [status, type, page]);
  useEffect(() => { void load(); const t = setInterval(() => void load(), 10000); return () => clearInterval(t); }, [load]);

  const count = (s: string) => stats.filter(x => x.status === s).reduce((n, x) => n + x.count, 0);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <main className="card full">
      <h1>Background jobs</h1>
      <p className="muted">Work done outside a web request, such as compressing a video. A job that fails is retried automatically with growing delays; one marked <b>failed</b> has used all its attempts or can never succeed.</p>
      <p className="row" data-testid="job-counts">
        {["pending", "running", "done", "failed"].map(s => <span key={s} className={`pill ${s === "failed" && count(s) > 0 ? "bad" : s === "done" ? "ok" : ""}`}>{s}: {count(s)}</span>)}
      </p>
      <div className="toolbar">
        <select aria-label="Status" value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}>{STATUSES.map(s => <option key={s} value={s}>{s || "Any status"}</option>)}</select>
        <select aria-label="Job type" value={type} onChange={e => { setType(e.target.value); setPage(1); }}><option value="">Any type</option>{[...new Set(stats.map(s => s.type))].map(t => <option key={t} value={t}>{t}</option>)}</select>
        <button className="btn secondary" onClick={() => void load()}>Refresh</button>
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (
        <>
          <div className="table-wrap"><table className="t"><thead><tr><th>#</th><th>Type</th><th>Status</th><th>Attempts</th><th>Next run / finished</th><th>Last error</th><th /></tr></thead><tbody>
            {data.items.map(j => (
              <tr key={j.id}><td>{j.id}</td><td><code>{j.type}</code></td>
                <td><span className={`pill ${j.status === "failed" ? "bad" : j.status === "done" ? "ok" : ""}`}>{j.status}</span></td>
                <td>{j.attempts} / {j.maxAttempts}</td><td>{new Date(j.finishedAt ?? j.runAt).toLocaleString()}</td><td>{j.lastError ?? <span className="muted">—</span>}</td>
                <td>{j.status === "failed" && can("jobs.manage") && <button className="btn small secondary" onClick={async () => { try { await api.retryJob(j.id); await load(); } catch (e) { setError(errorMessage(e)); } }}>Retry</button>}</td></tr>
            ))}
            {data.items.length === 0 && <tr><td colSpan={7} className="muted">No jobs.</td></tr>}
          </tbody></table></div>
          <p className="row spread"><span className="muted">{data.total} job{data.total === 1 ? "" : "s"}</span>
            <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
        </>
      )}
    </main>
  );
}
