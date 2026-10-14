import { useEffect, useState } from "react";
import { api, googleSignInUrl } from "../api";

/** Renders only when the API reports Google sign-in as configured. */
export default function GoogleButton({ returnUrl = "/" }: { returnUrl?: string }) {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => { api.providers().then(p => setEnabled(p.google)).catch(() => setEnabled(false)); }, []);
  if (!enabled) return null;
  return (
    <>
      <div className="divider"><span>or</span></div>
      <a className="btn secondary" href={googleSignInUrl(returnUrl)}>Continue with Google</a>
    </>
  );
}
