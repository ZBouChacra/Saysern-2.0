import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type OutboxRow, type Paged } from "../api";

const STATUSES = ["", "queued", "sent", "failed", "skipped"];

/** Which emails were sent, waiting or failed. The content is never shown (it may contain sign-in style links). */
export default function Outbox() {
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<OutboxRow> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { setData(await api.emailOutbox(status, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [status, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <main className="card full">
      <h1>Email log</h1>
      <p className="muted">Every email the site tried to send. A message that fails is retried automatically; one marked <b>failed</b> used all its attempts (retry it from Background jobs once the mail server is fixed). The text of an email is removed as soon as it is sent.</p>
      <div className="toolbar"><select aria-label="Status" value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}>{STATUSES.map(s => <option key={s} value={s}>{s || "Any status"}</option>)}</select><button className="btn secondary" onClick={() => void load()}>Refresh</button></div>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>When</th><th>To</th><th>About</th><th>Subject</th><th>Status</th><th>Attempts</th><th>Problem</th></tr></thead><tbody>
          {data.items.map(m => (<tr key={m.id}><td>{new Date(m.createdAt).toLocaleString()}</td><td>{m.to}</td><td><code>{m.eventCode}</code></td><td>{m.subject}</td>
            <td><span className={`pill ${m.status === "failed" ? "bad" : m.status === "sent" ? "ok" : ""}`}>{m.status}</span></td><td>{m.attempts}</td><td>{m.lastError ?? <span className="muted">—</span>}</td></tr>))}
          {data.items.length === 0 && <tr><td colSpan={7} className="muted">No emails.</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{data.total} email{data.total === 1 ? "" : "s"}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
      </>)}
    </main>
  );
}
