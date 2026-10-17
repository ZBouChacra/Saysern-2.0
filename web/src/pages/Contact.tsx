import { useState, type FormEvent } from "react";
import { ApiError, api, errorMessage } from "../api";
import { useAuth } from "../auth";
import { LookupSelect } from "../components";
import { useConfig } from "../config";
import { getText, t } from "../texts";

export default function Contact() {
  const { user } = useAuth();
  const cfg = useConfig();
  const max = cfg.num("Contact.MaxMessageLength") || 4000;
  const [f, setF] = useState({ name: user ? `${user.firstName} ${user.lastName}`.trim() : "", email: user?.email ?? "", topic: undefined as string | undefined, subject: "", message: "", website: "" });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null); setErrors({});
    try { await api.contact({ ...f, language: document.documentElement.lang || undefined }); setSent(true); }
    catch (err) { if (err instanceof ApiError && err.fieldErrors) setErrors(err.fieldErrors); setError(errorMessage(err)); }
    finally { setBusy(false); }
  }

  if (sent) return <main className="card narrow"><h1>{getText("page.contact.title") ?? t("Contact us")}</h1><p role="status">{t("Thank you. We received your message and will reply to {email}.", { email: f.email })}</p></main>;
  return (
    <main className="card narrow">
      <h1>{getText("page.contact.title") ?? t("Contact us")}</h1>
      <p className="muted">{getText("page.contact.intro") ?? t("Send us a message and we will reply by email.")}</p>
      <form onSubmit={submit} noValidate>
        <label>{t("Your name")}<input value={f.name} maxLength={100} autoComplete="name" onChange={e => setF({ ...f, name: e.target.value })} required />{errors.name && <span className="field-error" role="alert">{errors.name}</span>}</label>
        <label>{t("Email")}<input type="email" value={f.email} maxLength={255} autoComplete="email" onChange={e => setF({ ...f, email: e.target.value })} required />{errors.email && <span className="field-error" role="alert">{errors.email}</span>}</label>
        <label>{t("What is it about?")}<LookupSelect type="contact_topic" value={f.topic} onChange={v => setF({ ...f, topic: v })} emptyLabel={t("Choose (optional)")} /></label>
        <label>{t("Subject")}<input value={f.subject} maxLength={150} onChange={e => setF({ ...f, subject: e.target.value })} required />{errors.subject && <span className="field-error" role="alert">{errors.subject}</span>}</label>
        <label>{t("Message")}<textarea rows={6} value={f.message} maxLength={max} onChange={e => setF({ ...f, message: e.target.value })} required /><span className="muted small">{f.message.length} / {max}</span>{errors.message && <span className="field-error" role="alert">{errors.message}</span>}</label>
        {/* a trap for simple bots: people never see or fill this in */}
        <div className="hp" aria-hidden="true"><label>{t("Website")}<input tabIndex={-1} autoComplete="off" value={f.website} onChange={e => setF({ ...f, website: e.target.value })} /></label></div>
        {error && !Object.keys(errors).length && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy || !f.name.trim() || !f.email.trim() || !f.subject.trim() || !f.message.trim()}>{busy ? t("Sending…") : t("Send message")}</button>
      </form>
    </main>
  );
}
