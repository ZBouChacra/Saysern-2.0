import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, errorMessage, type Receipt } from "../api";
import { useSession } from "../session";

const money = (n: number, c: string) => `${n.toFixed(2)} ${c}`;

/** One receipt, laid out to print (use Print: the menu and buttons are hidden on paper). */
export default function ReceiptPage() {
  const { id } = useParams();
  const { can } = useSession();
  const [r, setR] = useState<Receipt | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [form, setForm] = useState({ billedTo: "", billedDetails: "", notes: "" });
  useEffect(() => { api.receipt(Number(id)).then(x => { setR(x); setError(null); }).catch(e => setError(errorMessage(e))); }, [id]);

  if (error) return <main className="card"><p className="error" role="alert">{error}</p><Link to="/payments">Back to payments</Link></main>;
  if (!r) return <main className="card"><p className="muted">Loading…</p></main>;

  async function save() {
    try { setR(await api.updateReceiptBilling(r!.id, { billedTo: form.billedTo, billedDetails: form.billedDetails || undefined, notes: form.notes || undefined })); setEditing(false); setError(null); } catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="receipt-wrap">
      <div className="no-print row spread">
        <Link to="/payments">← All receipts</Link>
        <span className="row">
          {can("payments.manage") && !editing && <button className="btn secondary" onClick={() => { setForm({ billedTo: r.billedTo, billedDetails: r.billedDetails ?? "", notes: r.notes ?? "" }); setEditing(true); }}>Correct who it is made out to</button>}
          <button className="btn" onClick={() => window.print()}>Print</button>
        </span>
      </div>
      {editing && (
        <section className="card no-print" aria-label="Correct receipt">
          <p className="muted small">Only the name, address and notes can be corrected. The amount and the number never change.</p>
          <label>Made out to<input value={form.billedTo} maxLength={200} onChange={e => setForm({ ...form, billedTo: e.target.value })} /></label>
          <label>Address / tax number<textarea rows={2} maxLength={500} value={form.billedDetails} onChange={e => setForm({ ...form, billedDetails: e.target.value })} /></label>
          <label>Notes<input value={form.notes} maxLength={500} onChange={e => setForm({ ...form, notes: e.target.value })} /></label>
          <div className="row"><button className="btn" disabled={!form.billedTo.trim()} onClick={() => void save()}>Save</button><button className="btn secondary" onClick={() => setEditing(false)}>Cancel</button></div>
        </section>
      )}
      <article className="receipt" data-testid="receipt">
        {r.status === "void" && <div className="void-banner" role="note">VOID: {r.voidReason ?? "cancelled"}</div>}
        <header className="receipt-head">
          <div><h1>{r.companyName}</h1>{r.companyAddress && <div className="pre">{r.companyAddress}</div>}{r.companyTaxId && <div>Tax no. {r.companyTaxId}</div>}</div>
          <div className="receipt-no"><div className="muted">RECEIPT</div><div className="big" data-testid="receipt-number">{r.receiptNumber}</div><div>Received {r.receivedOn}</div></div>
        </header>
        <section><div className="muted">Received from</div><div className="strong">{r.billedTo}</div>{r.billedDetails && <div className="pre">{r.billedDetails}</div>}</section>
        <table className="t receipt-lines"><thead><tr><th>Description</th><th>Qty (days)</th><th>Unit price</th><th>Amount</th></tr></thead><tbody>
          <tr><td>{r.description}</td><td>{r.quantity}</td><td>{money(r.unitPrice, r.currency)}</td><td>{money(r.subtotal, r.currency)}</td></tr>
        </tbody><tfoot>
          {r.vatPercent > 0 && <tr><td colSpan={3}>VAT {r.vatPercent}%</td><td>{money(r.vatAmount, r.currency)}</td></tr>}
          <tr className="strong"><td colSpan={3}>Total paid</td><td data-testid="receipt-total">{money(r.total, r.currency)}</td></tr>
        </tfoot></table>
        <section className="receipt-pay"><span>Paid by {r.methodName ?? r.method}</span>{r.reference && <span> · Reference {r.reference}</span>}</section>
        {r.notes && <section className="pre">{r.notes}</section>}
        {r.footer && <footer className="muted">{r.footer}</footer>}
      </article>
    </main>
  );
}
