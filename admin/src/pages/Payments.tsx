import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage, type Paged, type PaymentRow, type PaymentSummary } from "../api";

/** Every receipt, with filters, and the money received in the chosen period. */
export default function Payments() {
  const [f, setF] = useState({ status: "", kind: "", from: "", to: "", search: "" });
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<PaymentRow> | null>(null);
  const [summary, setSummary] = useState<PaymentSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try {
      const [d, s] = await Promise.all([api.payments({ status: f.status, kind: f.kind, from: f.from, to: f.to, search: f.search, page }), api.paymentSummary(f.from || undefined, f.to || undefined)]);
      setData(d); setSummary(s); setError(null);
    } catch (e) { setError(errorMessage(e)); }
  }, [f, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const set = (patch: Partial<typeof f>) => { setF(x => ({ ...x, ...patch })); setPage(1); };

  return (
    <main className="card full">
      <h1>Payments and receipts</h1>
      <p className="muted">A receipt is issued when a booking (an ad or premium batch) is marked paid. Receipts are numbered one after another, and a number is never reused: cancelling a paid booking voids its receipt but keeps the number on record.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {summary && (
        <p data-testid="payments-summary" className="row">
          <span className="pill ok">Received: <b>{summary.total.toFixed(2)} USD</b></span>
          {summary.rows.map(r => <span key={r.kind + r.method} className="pill">{r.kind} · {r.method.replace("_", " ")}: {r.total.toFixed(2)} ({r.receipts})</span>)}
          {summary.voided > 0 && <span className="pill bad">void receipts: {summary.voided}</span>}
        </p>
      )}
      <div className="toolbar">
        <input aria-label="Search receipts" placeholder="Number, name, reference or description" value={f.search} onChange={e => set({ search: e.target.value })} />
        <select aria-label="Kind" value={f.kind} onChange={e => set({ kind: e.target.value })}><option value="">Ads and premium</option><option value="ad">Ads</option><option value="premium">Premium</option></select>
        <select aria-label="Status" value={f.status} onChange={e => set({ status: e.target.value })}><option value="">Any status</option><option value="issued">Issued</option><option value="void">Void</option></select>
        <label>From<input type="date" aria-label="Received from" value={f.from} onChange={e => set({ from: e.target.value })} /></label>
        <label>To<input type="date" aria-label="Received to" value={f.to} onChange={e => set({ to: e.target.value })} /></label>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>Receipt</th><th>Received</th><th>Made out to</th><th>For</th><th>Method</th><th>Total (USD)</th><th>Status</th></tr></thead><tbody>
          {data.items.map(p => (
            <tr key={p.id}><td><Link to={`/payments/${p.id}`}>{p.receiptNumber}</Link></td><td>{p.receivedOn}</td><td>{p.billedTo}</td><td>{p.description}</td><td>{p.method.replace("_", " ")}{p.reference ? ` · ${p.reference}` : ""}</td>
              <td>{p.total.toFixed(2)}</td><td><span className={`pill ${p.status === "void" ? "bad" : "ok"}`}>{p.status}</span></td></tr>))}
          {data.items.length === 0 && <tr><td colSpan={7} className="muted">No receipts.</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{data.total} receipt{data.total === 1 ? "" : "s"}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
      </>)}
    </main>
  );
}
