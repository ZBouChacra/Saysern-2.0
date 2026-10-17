import { useState, type FormEvent } from "react";
import { Link, useNavigate } from "react-router-dom";
import { ApiError, api, errorMessage } from "../api";
import { useAuth } from "../auth";
import { useLang } from "../config";
import { useConfig } from "../config";
import { validateRegistration } from "../validation";
import GoogleButton from "./GoogleButton";

import { t } from "../texts";
import { passwordHint } from "../i18n";
export default function Register() {
  const { setUser } = useAuth();
  const { lang } = useLang();
  const navigate = useNavigate();
  const cfg = useConfig();
  const rules = { minLength: cfg.num("Auth.PasswordMinLength"), requireLetter: cfg.bool("Auth.PasswordRequireLetter"), requireDigit: cfg.bool("Auth.PasswordRequireDigit") };
  const [form, setForm] = useState({ firstName: "", lastName: "", email: "", password: "" });
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const local = validateRegistration(form, rules);
    setFieldErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    try {
      const created = await api.register({ ...form, email: form.email.trim(), firstName: form.firstName.trim(), lastName: form.lastName.trim() });
      // the language they are using becomes their account's language (e-mails, next sign-in); not being able to save it must not stop the sign-up
      setUser(created.preferredLanguage === lang ? created : await api.updatePreferences(lang, created.timeZone).catch(() => created));
      navigate("/");
    } catch (err) {
      if (err instanceof ApiError && err.fieldErrors) setFieldErrors(err.fieldErrors);
      setError(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  const field = (k: keyof typeof form, label: string, type = "text", autoComplete?: string) => (
    <label>{label}
      <input type={type} autoComplete={autoComplete} value={form[k]} onChange={set(k)} aria-invalid={!!fieldErrors[k]} />
      {fieldErrors[k] && <span className="field-error">{fieldErrors[k]}</span>}
    </label>
  );

  return (
    <main className="card narrow">
      <h1>{t("Create your account")}</h1>
      <form onSubmit={submit} noValidate>
        <div className="grid2">{field("firstName", "First name", "text", "given-name")}{field("lastName", "Last name", "text", "family-name")}</div>
        {field("email", "Email", "email", "email")}
        {field("password", "Password", "password", "new-password")}
        <p className="hint">{passwordHint(rules.minLength, rules.requireLetter, rules.requireDigit)}</p>
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={busy}>{busy ? t("Creating…") : t("Create account")}</button>
      </form>
      <GoogleButton />
      <p className="muted">{t("Already registered?")}{" "}<Link to="/login">{t("Sign in")}</Link></p>
    </main>
  );
}
