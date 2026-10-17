import { useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { api, errorMessage } from "../api";
import { t } from "../texts";

/** The page the "switch my account back on" e-mail links to. The token is posted (never fetched by GET), so a mail scanner cannot re-enable anything. */
export default function Reenable() {
  const [params] = useSearchParams();
  const token = params.get("token") ?? "";
  const [state, setState] = useState<"working" | "done" | "failed">("working");
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    api.reenableConfirm(token).then(() => setState("done")).catch(e => { setError(errorMessage(e)); setState("failed"); });
  }, [token]);

  return (
    <main className="card narrow">
      <h1>{t("Your account")}</h1>
      {state === "working" && <p className="muted" role="status">{t("Switching your account back on…")}</p>}
      {state === "done" && <><p className="ok-text" role="status">{t("Your account is switched on again. Welcome back!")}</p><p><Link className="btn" to="/login">{t("Sign in")}</Link></p></>}
      {state === "failed" && <><p className="error" role="alert">{error}</p><p><Link to="/login">{t("Back to sign in")}</Link></p></>}
    </main>
  );
}
