import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { ApiError, api, errorMessage } from "../api";
import { useConfig } from "../config";

import { t } from "../texts";
import { passwordHint } from "../i18n";
export default function ResetPassword() {
  const [params] = useSearchParams();
  const token = params.get("token") ?? "";
  const cfg = useConfig();
  const [password, setPassword] = useState("");
  const [done, setDone] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const min = cfg.num("Auth.PasswordMinLength");

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await api.resetPassword(token, password); setDone(true); }
    catch (err) { setError(err instanceof ApiError && err.fieldErrors?.password ? err.fieldErrors.password : errorMessage(err)); }
    finally { setBusy(false); }
  }

  if (done) return <main className="card narrow"><h1>{t("Password changed")}</h1><p role="status">{t("Your password was changed and any other sessions were signed out.")}</p><p><Link className="btn" to="/login">{t("Sign in")}</Link></p></main>;
  return (
    <main className="card narrow">
      <h1>{t("Choose a new password")}</h1>
      <form onSubmit={submit} noValidate>
        <label>{t("New password")}<input type="password" autoComplete="new-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
        <p className="hint">{passwordHint(min, cfg.bool("Auth.PasswordRequireLetter"), cfg.bool("Auth.PasswordRequireDigit"))}</p>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy || !password}>{busy ? t("Saving…") : t("Change password")}</button>
      </form>
      <p className="muted"><Link to="/forgot-password">{t("Ask for a new link")}</Link></p>
    </main>
  );
}
