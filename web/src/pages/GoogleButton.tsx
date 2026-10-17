import { useEffect, useState } from "react";
import { api, googleSignInUrl } from "../api";

import { t } from "../texts";
/** Renders only when the API reports Google sign-in as configured. */
export default function GoogleButton({ returnUrl = "/" }: { returnUrl?: string }) {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => { api.providers().then(p => setEnabled(p.google)).catch(() => setEnabled(false)); }, []);
  if (!enabled) return null;
  return (
    <>
      <div className="divider"><span>{t("or")}</span></div>
      <a className="btn secondary" href={googleSignInUrl(returnUrl)}>{t("Continue with Google")}</a>
    </>
  );
}
