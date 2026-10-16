import { useState, type FormEvent } from "react";
import { api, errorMessage } from "../api";
import { useSession } from "../session";

export default function Security() {
  const { session, setSession } = useSession();
  const [code, setCode] = useState("");
  const [codes, setCodes] = useState<string[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  async function regenerate(e: FormEvent) {
    e.preventDefault();
    try { const r = await api.regenerateRecoveryCodes(code); setCodes(r.recoveryCodes); setCode(""); setError(null); setSession(await api.me()); } catch (err) { setError(errorMessage(err)); }
  }
  return (
    <main className="card full">
      <h1>My security</h1>
      <p>Signed in as <b>{session!.user.email}</b>. Two-factor sign-in is on. Your session ends after {session!.idleMinutes} minutes without activity.</p>
      <p>Unused recovery codes: <b data-testid="recovery-remaining">{session!.recoveryCodesRemaining}</b></p>
      <form onSubmit={regenerate} className="toolbar"><label>Authenticator code<input inputMode="numeric" autoComplete="one-time-code" value={code} onChange={e => setCode(e.target.value)} /></label><button className="btn" disabled={!code.trim()}>Generate new recovery codes</button></form>
      <p className="hint">This replaces your current codes: the old ones stop working.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {codes && <><p><b>Save these now. They are shown only once.</b></p><ul className="codes">{codes.map(c => <li key={c}><code>{c}</code></li>)}</ul></>}
    </main>
  );
}
