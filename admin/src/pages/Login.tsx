import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import QRCode from "qrcode";
import { api, errorMessage } from "../api";
import { useSession } from "../session";

import { t } from "../texts";
type Step = "password" | "code" | "enroll" | "recovery-codes";

export default function Login() {
  const { setSession } = useSession();
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>("password");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [useRecovery, setUseRecovery] = useState(false);
  const [enroll, setEnroll] = useState<{ secret: string; qr: string } | null>(null);
  const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function run(fn: () => Promise<void>) {
    setBusy(true); setError(null);
    try { await fn(); } catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  }

  const submitPassword = (e: FormEvent) => { e.preventDefault(); void run(async () => {
    const r = await api.login(email, password);
    setPassword(""); setCode("");
    if (r.status === "totp_required") { setStep("code"); return; }
    const info = await api.enroll();
    setEnroll({ secret: info.secret, qr: await QRCode.toDataURL(info.otpauthUri, { margin: 1, width: 220 }) });
    setStep("enroll");
  }); };

  const submitCode = (e: FormEvent) => { e.preventDefault(); void run(async () => {
    const r = await api.verify(useRecovery ? { recoveryCode: code } : { code });
    setSession(r.session); navigate("/");
  }); };

  const submitEnroll = (e: FormEvent) => { e.preventDefault(); void run(async () => {
    const r = await api.confirm(code);
    setRecoveryCodes(r.recoveryCodes); setPendingSession(r.session); setStep("recovery-codes");
  }); };
  const [pendingSession, setPendingSession] = useState<Awaited<ReturnType<typeof api.me>> | null>(null);

  return (
    <main className="card narrow">
      <h1>{t("Back office")}</h1>
      {step === "password" && (
        <form onSubmit={submitPassword} noValidate>
          <label>{t("Email")}<input type="email" autoComplete="username" value={email} onChange={e => setEmail(e.target.value)} required /></label>
          <label>{t("Password")}<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="btn" disabled={busy}>{busy ? t("Checking…") : t("Continue")}</button>
          <p className="hint">{t("Two-factor sign-in is required for every back-office account.")}</p>
        </form>
      )}
      {step === "code" && (
        <form onSubmit={submitCode} noValidate>
          <p>{useRecovery ? t("Enter one of your recovery codes. Each works once.") : t("Enter the 6-digit code from your authenticator app.")}</p>
          <label>{useRecovery ? t("Recovery code") : t("Authenticator code")}
            <input inputMode={useRecovery ? "text" : "numeric"} autoComplete="one-time-code" value={code} onChange={e => setCode(e.target.value)} autoFocus required /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="btn" disabled={busy || code.trim().length === 0}>{busy ? t("Checking…") : t("Sign in")}</button>
          <p><button type="button" className="link" onClick={() => { setUseRecovery(!useRecovery); setCode(""); setError(null); }}>{useRecovery ? t("Use my authenticator instead") : t("I lost my device: use a recovery code")}</button></p>
        </form>
      )}
      {step === "enroll" && enroll && (
        <form onSubmit={submitEnroll} noValidate>
          <p><b>{t("Set up your authenticator.")}</b>{" "}{t("Scan this with Google Authenticator, Microsoft Authenticator, Authy or 1Password, then enter the 6-digit code it shows.")}</p>
          <img src={enroll.qr} alt={t("Authenticator QR code")} width={220} height={220} />
          <p className="hint">{t("Can't scan? Enter this key manually:")}{" "}<code data-testid="totp-secret">{enroll.secret}</code></p>
          <label>{t("6-digit code")}<input inputMode="numeric" autoComplete="one-time-code" value={code} onChange={e => setCode(e.target.value)} autoFocus required /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <button className="btn" disabled={busy || code.trim().length === 0}>{busy ? t("Checking…") : t("Confirm and continue")}</button>
        </form>
      )}
      {step === "recovery-codes" && (
        <div>
          <p><b>{t("Save your recovery codes.")}</b>{" "}{t("If you lose your device, each of these signs you in once. They are shown only now.")}</p>
          <ul className="codes" data-testid="recovery-codes">{recoveryCodes.map(c => <li key={c}><code>{c}</code></li>)}</ul>
          <p className="row"><button className="btn secondary" onClick={() => void navigator.clipboard?.writeText(recoveryCodes.join("\n"))}>{t("Copy")}</button>
            <button className="btn" onClick={() => { setSession(pendingSession); navigate("/"); }}>{t("I have saved them")}</button></p>
        </div>
      )}
    </main>
  );
}
