import { useEffect, useState } from "react";
import { api, errorMessage, type BillingInput, type LookupValue } from "../api";

const today = () => new Date().toISOString().slice(0, 10);

/**
 * Recording a payment: who it is for and how it was paid. Submitting marks the batch paid AND issues the numbered receipt, in one step
 * (if anything fails, nothing is recorded and no receipt number is used).
 */
export default function PaymentForm({ title, defaultBilledTo, amount, onSubmit, onCancel }: { title: string; defaultBilledTo: string; amount: number; onSubmit: (b: BillingInput) => Promise<void>; onCancel: () => void }) {
  const [methods, setMethods] = useState<LookupValue[]>([]);
  const [f, setF] = useState({ billedTo: defaultBilledTo, billedDetails: "", method: "", reference: "", receivedOn: today(), notes: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => { api.lookupValues("payment_method").then(v => { const active = v.filter(x => x.isActive); setMethods(active); setF(x => ({ ...x, method: x.method || active[0]?.code || "" })); }).catch(e => setError(errorMessage(e))); }, []);

  async function submit() {
    setBusy(true); setError(null);
    try { await onSubmit({ billedTo: f.billedTo.trim(), billedDetails: f.billedDetails.trim() || undefined, method: f.method, reference: f.reference.trim() || undefined, receivedOn: f.receivedOn || undefined, notes: f.notes.trim() || undefined }); }
    catch (e) { setError(errorMessage(e)); setBusy(false); }
  }

  return (
    <section className="card" aria-label="Record payment">
      <h3>Record payment: {title}</h3>
      <p className="muted small">Amount: <b>{amount.toFixed(2)} USD</b> (VAT, if you charge it, is added as set in Settings → Billing). This marks the booking paid and issues the receipt.</p>
      {error && <p className="error" role="alert">{error}</p>}
      <label>Receipt made out to<input value={f.billedTo} maxLength={200} onChange={e => setF({ ...f, billedTo: e.target.value })} /></label>
      <label>Address / tax number (optional)<textarea rows={2} maxLength={500} value={f.billedDetails} onChange={e => setF({ ...f, billedDetails: e.target.value })} /></label>
      <div className="row">
        <label>How was it paid?<select value={f.method} onChange={e => setF({ ...f, method: e.target.value })}>{methods.map(m => <option key={m.code} value={m.code}>{m.names.en ?? m.code}</option>)}</select></label>
        <label>Reference (optional)<input value={f.reference} maxLength={100} onChange={e => setF({ ...f, reference: e.target.value })} /></label>
        <label>Received on<input type="date" max={today()} value={f.receivedOn} onChange={e => setF({ ...f, receivedOn: e.target.value })} /></label>
      </div>
      <label>Notes (optional)<input value={f.notes} maxLength={500} onChange={e => setF({ ...f, notes: e.target.value })} /></label>
      <div className="row"><button className="btn" disabled={busy || !f.billedTo.trim() || !f.method} onClick={() => void submit()}>{busy ? "Recording…" : "Record payment and issue receipt"}</button><button className="btn secondary" disabled={busy} onClick={onCancel}>Cancel</button></div>
    </section>
  );
}
