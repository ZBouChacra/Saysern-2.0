import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, errorMessage } from "../api";
import { useAuth } from "../auth";

import { t } from "../texts";
import { tx } from "../i18n";
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
      <h1>{t("Email address")}</h1>
      {state === "working" && <p className="muted" role="status">{t("Confirming your address…")}</p>}
      {state === "done" && <><p className="ok-text" role="status">{t("Thank you: your email address is confirmed.")}</p><p><Link className="btn" to={user ? "/" : "/login"}>{user ? t("Continue") : t("Sign in")}</Link></p></>}
      {state === "failed" && <><p className="error" role="alert">{error}</p>{user ? <p className="muted">{t("You can ask for a new link from the notice at the top of the page.")}</p> : <p>{tx("{link} to ask for a new link.", { link: <Link to="/login">{t("Sign in")}</Link> })}</p>}</>}
    </main>
  );
}
