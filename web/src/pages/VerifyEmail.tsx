import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, errorMessage } from "../api";
import { useAuth } from "../auth";

/** The page the verification e-mail links to. The token is posted (not fetched with GET), so a mail scanner opening the link cannot verify anything by accident. */
export default function VerifyEmail() {
  const [params] = useSearchParams();
  const token = params.get("token") ?? "";
  const { user, setUser } = useAuth();
  const [state, setState] = useState<"working" | "done" | "failed">("working");
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;   // React may run effects twice in development; the link works only once
    started.current = true;
    api.verifyEmail(token)
      .then(() => { setState("done"); if (user) setUser({ ...user, emailVerified: true }); })
      .catch(e => { setError(errorMessage(e)); setState("failed"); });
  }, [token, user, setUser]);

  return (
    <main className="card narrow">
      <h1>Email address</h1>
      {state === "working" && <p className="muted" role="status">Confirming your address…</p>}
      {state === "done" && <><p className="ok-text" role="status">Thank you: your email address is confirmed.</p><p><Link className="btn" to={user ? "/" : "/login"}>{user ? "Continue" : "Sign in"}</Link></p></>}
      {state === "failed" && <><p className="error" role="alert">{error}</p>{user ? <p className="muted">You can ask for a new link from the notice at the top of the page.</p> : <p><Link to="/login">Sign in</Link> to ask for a new link.</p>}</>}
    </main>
  );
}
