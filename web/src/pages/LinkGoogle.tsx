import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { api, errorMessage } from "../api";
import { useAuth } from "../auth";

/** Shown when a Google sign-in uses an email that already has a password account. Ownership must be proven first. */
export default function LinkGoogle() {
  const { setUser } = useAuth();
  const navigate = useNavigate();
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setUser(await api.linkPending(password));
      navigate("/");
    } catch (err) {
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="card narrow">
      <h1>Link your Google account</h1>
      <p>An account with your Google email address already exists. To connect Google to it, confirm it's yours by entering its password.</p>
      <form onSubmit={submit} noValidate>
        <label>Password<input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} required /></label>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy || password.length === 0}>{busy ? "Linking…" : "Confirm and link"}</button>
      </form>
      <p className="muted"><Link to="/login">Cancel</Link></p>
    </main>
  );
}
