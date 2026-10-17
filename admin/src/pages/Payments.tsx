import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage, type Paged, type PaymentRow, type PaymentSummary, type LookupValue } from "../api";

import { t } from "../texts";
import { tn, statusLabel } from "../i18n";
/** Every receipt, with filters, and the money received in the chosen period. */
export default function Payments() {
  const [methods, setMethods] = useState<LookupValue[]>([]);
  useEffect(() => { api.lookupValues("payment_method").then(setMethods).catch(() => setMethods([])); }, []);
  const methodName = (code: string) => { const v = methods.find(x => x.code === code); return v ? (v.names[document.documentElement.lang] ?? v.names.en ?? code) : code; };
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
      <h1>{t("Payments and receipts")}</h1>
      <p className="muted">{t("A receipt is issued when a booking (an ad or premium batch) is marked paid. Receipts are numbered one after another, and a number is never reused: cancelling a paid booking voids its receipt but keeps the number on record.")}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {summary && (
        <p data-testid="payments-summary" className="row">
          <span className="pill ok">{t("Received:")}{" "}<b>{t("{toFixed} USD", { toFixed: summary.total.toFixed(2) })}</b></span>
          {summary.rows.map(r => <span key={r.kind + r.method} className="pill">{r.kind === "ad" ? t("Ads") : t("Premium")} · {methodName(r.method)}: {r.total.toFixed(2)} ({r.receipts})</span>)}
          {summary.voided > 0 && <span className="pill bad">{t("void receipts: {voided}", { voided: summary.voided })}</span>}
        </p>
      )}
      <div className="toolbar">
        <input aria-label={t("Search receipts")} placeholder={t("Number, name, reference or description")} value={f.search} onChange={e => set({ search: e.target.value })} />
        <select aria-label={t("Kind")} value={f.kind} onChange={e => set({ kind: e.target.value })}><option value="">{t("Ads and premium")}</option><option value="ad">{t("Ads")}</option><option value="premium">{t("Premium")}</option></select>
        <select aria-label={t("Status")} value={f.status} onChange={e => set({ status: e.target.value })}><option value="">{t("Any status")}</option><option value="issued">{t("Issued")}</option><option value="void">{t("Void")}</option></select>
        <label>{t("From")}<input type="date" aria-label={t("Received from")} value={f.from} onChange={e => set({ from: e.target.value })} /></label>
        <label>{t("To")}<input type="date" aria-label={t("Received to")} value={f.to} onChange={e => set({ to: e.target.value })} /></label>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>{t("Receipt")}</th><th>{t("Received")}</th><th>{t("Made out to")}</th><th>{t("For")}</th><th>{t("Method")}</th><th>{t("Total (USD)")}</th><th>{t("Status")}</th></tr></thead><tbody>
          {data.items.map(p => (
            <tr key={p.id}><td><Link to={`/payments/${p.id}`}>{p.receiptNumber}</Link></td><td>{p.receivedOn}</td><td>{p.billedTo}</td><td>{p.description}</td><td>{p.method.replace("_", " ")}{p.reference ? ` · ${p.reference}` : ""}</td>
              <td>{p.total.toFixed(2)}</td><td><span className={`pill ${p.status === "void" ? "bad" : "ok"}`}>{statusLabel(p.status)}</span></td></tr>))}
          {data.items.length === 0 && <tr><td colSpan={7} className="muted">{t("No receipts.")}</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{tn(data.total, "1 receipt", "{n} receipts")}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("Previous")}</button><span>{t("Page {page} of {pages}", { page, pages })}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("Next")}</button></span></p>
      </>)}
    </main>
  );
}
