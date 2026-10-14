import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, googleLinkUrl } from "../api";
import { useAuth } from "../auth";

export default function Home() {
  const { user, loading } = useAuth();
  const [google, setGoogle] = useState(false);
  const [params] = useSearchParams();
  useEffect(() => { api.providers().then(p => setGoogle(p.google)).catch(() => setGoogle(false)); }, []);

  if (loading) return <p className="muted">Loading…</p>;
  if (!user) {
    return (
      <main className="card">
        <h1>Find the right professional for every need</h1>
        <p>Sign in or create an account to continue.</p>
        <p className="row"><Link className="btn" to="/login">Sign in</Link><Link className="btn secondary" to="/register">Create account</Link></p>
      </main>
    );
  }
  return (
    <main className="card">
      <h1>Welcome, {user.firstName}</h1>
      <dl>
        <dt>Email</dt><dd>{user.email} {user.emailVerified ? <span className="pill ok">verified</span> : <span className="pill">unverified</span>}</dd>
        <dt>Role</dt><dd>{user.role}</dd>
      </dl>
      {params.get("google") === "already_linked" && <p className="error" role="alert">That Google account is already linked to a different user.</p>}
      {google && <p><a className="btn secondary" href={googleLinkUrl("/")}>Link a Google account</a></p>}
      {user.role === "admin" && <p><Link className="btn" to="/admin">Open the back office</Link></p>}
    </main>
  );
}
