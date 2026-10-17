import { useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { ApiError, api, errorMessage } from "../api";
import { useAuth } from "../auth";
import { t } from "../texts";
import GoogleButton from "./GoogleButton";

export default function Login() {
  const { setUser } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(params.get("error") ? errorMessage(params.get("error")) : null);
  const [disabled, setDisabled] = useState(params.get("error") === "account_disabled");   // from Google: the link has already been sent
  const [linkSent, setLinkSent] = useState(params.get("error") === "account_disabled");
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null); setDisabled(false); setLinkSent(false);
    try {
      setUser(await api.login(email, password));
      navigate("/");
    } catch (err) {
      if (err instanceof ApiError && err.code === "ACCOUNT_DISABLED") setDisabled(true); else setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  // The right password was just entered, so we may send the e-mail that switches the account back on.
  async function sendLink() {
    setBusy(true);
    try { await api.reenableRequest(email, password); setLinkSent(true); } catch (err) { setError(errorMessage(err)); } finally { setBusy(false); }
  }

  return (
    <main className="card narrow">
      <h1>{t("Welcome back")}</h1>
      <form onSubmit={submit} noValidate>
        <label>{t("Email")}<input type="email" autoComplete="email" value={email} onChange={e => setEmail(e.target.value)} required /></label>
        <label>{t("Password")}<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
        {error && <p className="error" role="alert">{error}</p>}
        {disabled && (
          <div className="notice-box" role="alert" data-testid="account-disabled">
            <p><strong>{t("Your account is disabled.")}</strong> {t("Nothing was deleted. We can email you a link to switch it back on.")}</p>
            {linkSent ? <p className="ok-text" role="status">{t("If your details are right, we sent the link. Please check your inbox.")}</p>
              : <button type="button" className="btn secondary" disabled={busy || !email || !password} onClick={() => void sendLink()}>{t("Email me the link")}</button>}
          </div>
        )}
        <button className="btn" disabled={busy}>{busy ? t("Signing in…") : t("Sign in")}</button>
      </form>
      <GoogleButton />
      <p className="muted"><Link to="/forgot-password">{t("Forgot your password?")}</Link></p>
      <p className="muted">{t("No account yet?")} <Link to="/register">{t("Create one")}</Link></p>
    </main>
  );
}
