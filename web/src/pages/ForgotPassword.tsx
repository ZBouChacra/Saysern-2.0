import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage } from "../api";

import { t } from "../texts";
export default function ForgotPassword() {
  const [email, setEmail] = useState("");
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await api.forgotPassword(email); setSent(true); } catch (err) { setError(errorMessage(err)); } finally { setBusy(false); }
  }

  return (
    <main className="card narrow">
      <h1>{t("Forgot your password?")}</h1>
      {sent ? (
        <p role="status">{t("If there is an account for {email}, we have sent it an email with a link to choose a new password. The link works once and expires soon.", { email })}</p>
      ) : (
        <form onSubmit={submit} noValidate>
          <p className="muted">{t("Enter the email address of your account and we will send you a link.")}</p>
          <label>{t("Email")}<input type="email" autoComplete="email" value={email} onChange={e => setEmail(e.target.value)} required /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="btn" disabled={busy || !email.trim()}>{busy ? t("Sending…") : t("Send the link")}</button>
        </form>
      )}
      <p className="muted"><Link to="/login">{t("Back to sign in")}</Link></p>
    </main>
  );
}
