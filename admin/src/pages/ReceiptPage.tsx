import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { api, errorMessage, type Receipt } from "../api";
import { useSession } from "../session";

import { useLanguage } from "../language";
import { t, translatorFor } from "../texts";
const money = (n: number, c: string) => `${n.toFixed(2)} ${c}`;

/** One receipt, laid out to print (use Print: the menu and buttons are hidden on paper). */
export default function ReceiptPage() {
  const { id } = useParams();
  const { can } = useSession();
  const [r, setR] = useState<Receipt | null>(null);
  const [error, setError] = useState<string | null>(null);
  const { lang, languages } = useLanguage();
  const [rl, setRl] = useState(lang);                                  // the language the receipt is printed in (the customer's), not necessarily the screen's
  const [map, setMap] = useState<Record<string, string>>({});
  const tl = translatorFor(map);
  useEffect(() => { api.translations(rl).then(setMap).catch(() => setMap({})); }, [rl]);
  const [editing, setEditing] = useState(false);
  const [form, setForm] = useState({ billedTo: "", billedDetails: "", notes: "" });
  useEffect(() => { api.receipt(Number(id)).then(x => { setR(x); setError(null); }).catch(e => setError(errorMessage(e))); }, [id]);

  if (error) return <main className="card"><p className="error" role="alert">{error}</p><Link to="/payments">{t("Back to payments")}</Link></main>;
  if (!r) return <main className="card"><p className="muted">{t("Loading…")}</p></main>;

  async function save() {
    try { setR(await api.updateReceiptBilling(r!.id, { billedTo: form.billedTo, billedDetails: form.billedDetails || undefined, notes: form.notes || undefined })); setEditing(false); setError(null); } catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="receipt-wrap">
      <div className="no-print row spread">
        <Link to="/payments">{t("← All receipts")}</Link>
        <span className="row">
          {can("payments.manage") && !editing && <button className="btn secondary" onClick={() => { setForm({ billedTo: r.billedTo, billedDetails: r.billedDetails ?? "", notes: r.notes ?? "" }); setEditing(true); }}>{t("Correct who it is made out to")}</button>}
          <label className="row">{t("Receipt language")}<select value={rl} onChange={e => setRl(e.target.value)}>{languages.map(l => <option key={l.code} value={l.code}>{l.name}</option>)}</select></label>
          <button className="btn" onClick={() => window.print()}>{t("Print")}</button>
        </span>
      </div>
      {editing && (
        <section className="card no-print" aria-label={t("Correct receipt")}>
          <p className="muted small">{t("Only the name, address and notes can be corrected. The amount and the number never change.")}</p>
          <label>{t("Made out to")}<input value={form.billedTo} maxLength={200} onChange={e => setForm({ ...form, billedTo: e.target.value })} /></label>
          <label>{t("Address / tax number")}<textarea rows={2} maxLength={500} value={form.billedDetails} onChange={e => setForm({ ...form, billedDetails: e.target.value })} /></label>
          <label>{t("Notes")}<input value={form.notes} maxLength={500} onChange={e => setForm({ ...form, notes: e.target.value })} /></label>
          <div className="row"><button className="btn" disabled={!form.billedTo.trim()} onClick={() => void save()}>{t("Save")}</button><button className="btn secondary" onClick={() => setEditing(false)}>{t("Cancel")}</button></div>
        </section>
      )}
      <article className="receipt" data-testid="receipt" lang={rl} dir={languages.find(l => l.code === rl)?.dir ?? "ltr"}>
        {r.status === "void" && <div className="void-banner" role="note">{tl("VOID:")}{" "}{r.voidReason ?? "cancelled"}</div>}
        <header className="receipt-head">
          <div><h1>{r.companyName}</h1>{r.companyAddress && <div className="pre">{r.companyAddress}</div>}{r.companyTaxId && <div>{tl("Tax no. {companyTaxId}", { companyTaxId: r.companyTaxId })}</div>}</div>
          <div className="receipt-no"><div className="muted">{tl("RECEIPT")}</div><div className="big" data-testid="receipt-number">{r.receiptNumber}</div><div>{tl("Received {receivedOn}", { receivedOn: r.receivedOn })}</div></div>
        </header>
        <section><div className="muted">{tl("Received from")}</div><div className="strong">{r.billedTo}</div>{r.billedDetails && <div className="pre">{r.billedDetails}</div>}</section>
        <table className="t receipt-lines"><thead><tr><th>{tl("Description")}</th><th>{tl("Qty (days)")}</th><th>{tl("Unit price")}</th><th>{tl("Amount")}</th></tr></thead><tbody>
          <tr><td>{rl === "ar" ? (r.descriptionAr ?? r.description) : r.description}</td><td>{r.quantity}</td><td>{money(r.unitPrice, r.currency)}</td><td>{money(r.subtotal, r.currency)}</td></tr>
        </tbody><tfoot>
          {r.vatPercent > 0 && <tr><td colSpan={3}>{tl("VAT {vatPercent}%", { vatPercent: r.vatPercent })}</td><td>{money(r.vatAmount, r.currency)}</td></tr>}
          <tr className="strong"><td colSpan={3}>{tl("Total paid")}</td><td data-testid="receipt-total">{money(r.total, r.currency)}</td></tr>
        </tfoot></table>
        <section className="receipt-pay"><span>{tl("Paid by")}{" "}{(rl === "ar" ? r.methodNameAr : undefined) ?? r.methodName ?? r.method}</span>{r.reference && <span>{" "}{tl("· Reference {reference}", { reference: r.reference })}</span>}</section>
        {r.notes && <section className="pre">{r.notes}</section>}
        {r.footer && <footer className="muted">{r.footer}</footer>}
      </article>
    </main>
  );
}
