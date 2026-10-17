import { useEffect, useState, type FormEvent } from "react";
import { ApiError, api, errorMessage, type NotificationPref } from "../api";
import { useAuth } from "../auth";
import { useLang, useLookup } from "../config";
import { t } from "../texts";
import { applyTheme, getTheme, setTheme, type ThemeChoice } from "../theme";
import { setTimeZone } from "../time";

/** Everything a person can set about themselves: language, time zone, appearance, notifications, password, and disabling the account. */
export default function Settings() {
  return (
    <main className="card wide">
      <h1>{t("Settings")}</h1>
      <LanguageAndTime />
      <Appearance />
      <Notifications />
      <Password />
      <DisableAccount />
    </main>
  );
}

function LanguageAndTime() {
  const { user, setUser } = useAuth();
  const { lang, setLang, languages } = useLang();
  const zones = useLookup("time_zone");
  const [zone, setZone] = useState(user?.timeZone ?? "");
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function save(language: string, timeZone: string) {
    try {
      const updated = await api.updatePreferences(language, timeZone || undefined);
      setTimeZone(updated.timeZone); setUser({ ...updated }); setError(null); setMessage(t("Saved."));
      if (language !== lang) setLang(language);   // switching the language reloads the texts and the direction
    } catch (e) { setError(errorMessage(e)); setMessage(null); }
  }

  return (
    <section aria-label={t("Language and time")}>
      <h2>{t("Language and time")}</h2>
      <div className="row">
        <label>{t("Language")}
          <select value={lang} onChange={e => void save(e.target.value, zone)}>{languages.map(l => <option key={l.code} value={l.code}>{l.names[l.code] ?? l.names.en ?? l.code}</option>)}</select>
        </label>
        <label>{t("Time zone")}
          <select value={zone} onChange={e => { setZone(e.target.value); void save(lang, e.target.value); }}>
            <option value="">{t("Use the site's default")}</option>
            {zones.values.map(z => <option key={z.code} value={z.code}>{zones.name(z)}</option>)}
          </select>
        </label>
      </div>
      <p className="muted small">{t("Times in emails, receipts and your appointments are shown in your time zone.")}</p>
      {message && <p className="ok-text" role="status">{message}</p>}
      {error && <p className="error" role="alert">{error}</p>}
    </section>
  );
}

function Appearance() {
  const [theme, setThemeState] = useState<ThemeChoice>(getTheme());
  useEffect(() => { applyTheme(theme); }, [theme]);
  return (
    <section aria-label={t("Appearance")}>
      <h2>{t("Appearance")}</h2>
      <label>{t("Theme")}
        <select value={theme} onChange={e => { const v = e.target.value as ThemeChoice; setTheme(v); setThemeState(v); }}>
          <option value="system">{t("Same as my device")}</option><option value="light">{t("Light")}</option><option value="dark">{t("Dark")}</option>
        </select>
      </label>
    </section>
  );
}

/** Events and channels come from lookups an admin maintains; a channel that is switched off is simply not offered. */
function Notifications() {
  const events = useLookup("notification_event");
  const channels = useLookup("notification_channel");
  const [prefs, setPrefs] = useState<NotificationPref[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.notificationPrefs().then(setPrefs).catch(e => setError(errorMessage(e))); }, []);

  const on = (event: string, channel: string) => prefs?.find(p => p.event === event && p.channel === channel)?.enabled ?? true;   // no choice recorded = on
  async function toggle(event: string, channel: string) {
    const before = prefs;
    const enabled = !on(event, channel);
    setPrefs([...(prefs ?? []).filter(p => !(p.event === event && p.channel === channel)), { event, channel, enabled }]);   // respond at once...
    try { setPrefs(await api.setNotificationPref(event, channel, enabled)); setError(null); }
    catch (e) { setPrefs(before); setError(errorMessage(e)); }                                                              // ...and put it back if the server refuses
  }

  return (
    <section aria-label={t("Notifications")}>
      <h2>{t("Notifications")}</h2>
      <p className="muted">{t("Choose when we email you. Messages about your account itself (confirming your address, resetting your password) are always sent.")}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {prefs === null || events.loading || channels.loading ? <p className="muted">{t("Loading…")}</p> : (
        <div className="table-wrap"><table className="t"><thead><tr><th>{t("Email me when…")}</th>{channels.values.map(c => <th key={c.code}>{channels.name(c)}</th>)}</tr></thead><tbody>
          {events.values.map(ev => {
            const mandatory = ev.metadata?.mandatory === true;
            return (
              <tr key={ev.code}><td>{events.name(ev)}{mandatory && <span className="muted small"> {t("(always sent)")}</span>}</td>
                {channels.values.map(c => <td key={c.code}>{mandatory ? <input type="checkbox" checked disabled aria-label={`${events.name(ev)} (${channels.name(c)}): ${t("always on")}`} />
                  : <input type="checkbox" checked={on(ev.code, c.code)} onChange={() => void toggle(ev.code, c.code)} aria-label={`${events.name(ev)} (${channels.name(c)})`} />}</td>)}
              </tr>
            );
          })}
        </tbody></table></div>
      )}
    </section>
  );
}

function Password() {
  const { user, setUser } = useAuth();
  const [f, setF] = useState({ current: "", next: "" });
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!user) return null;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setErrors({}); setError(null); setMessage(null);
    try { setUser(await api.changePassword(f.current, f.next)); setF({ current: "", next: "" }); setMessage(t("Your password was changed. You were signed out on your other devices.")); }
    catch (err) { if (err instanceof ApiError && err.fieldErrors) setErrors(err.fieldErrors); setError(errorMessage(err)); }
    finally { setBusy(false); }
  }

  return (
    <section aria-label={t("Password")}>
      <h2>{t("Password")}</h2>
      <form onSubmit={submit} noValidate>
        <label>{t("Current password")}<input type="password" autoComplete="current-password" value={f.current} onChange={e => setF({ ...f, current: e.target.value })} />{errors.currentPassword && <span className="field-error" role="alert">{errors.currentPassword}</span>}</label>
        <label>{t("New password")}<input type="password" autoComplete="new-password" value={f.next} onChange={e => setF({ ...f, next: e.target.value })} />{errors.newPassword && <span className="field-error" role="alert">{errors.newPassword}</span>}</label>
        {error && !Object.keys(errors).length && <p className="error" role="alert">{error}</p>}
        {message && <p className="ok-text" role="status">{message}</p>}
        <button className="btn" disabled={busy || !f.current || !f.next}>{busy ? t("Saving…") : t("Change password")}</button>
      </form>
      <p className="muted small">{t("Signed up with Google and no password yet? Use “Forgot your password?” on the sign-in page to choose one.")}</p>
    </section>
  );
}

function DisableAccount() {
  const { user, setUser } = useAuth();
  const [open, setOpen] = useState(false);
  const [f, setF] = useState({ password: "", confirmEmail: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (!user) return null;

  async function disable(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await api.disableAccount({ password: f.password || undefined, confirmEmail: f.confirmEmail || undefined }); setUser(null); window.location.assign("/login"); }
    catch (err) { setError(errorMessage(err)); setBusy(false); }
  }

  return (
    <section aria-label={t("Disable my account")}>
      <h2>{t("Disable my account")}</h2>
      <p className="muted">{t("Your profile is hidden and you are signed out. Nothing is deleted; you can switch the account back on by email.")}</p>
      {!open ? <button className="btn secondary" onClick={() => setOpen(true)}>{t("Disable my account…")}</button> : (
        <form onSubmit={disable} noValidate>
          <label>{t("Your password")}<input type="password" autoComplete="current-password" value={f.password} onChange={e => setF({ ...f, password: e.target.value })} /></label>
          <p className="muted small">{t("Signed up with Google? Type your email address instead.")}</p>
          <label>{t("Email address")}<input type="email" autoComplete="email" value={f.confirmEmail} onChange={e => setF({ ...f, confirmEmail: e.target.value })} /></label>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="row"><button className="btn danger" disabled={busy || (!f.password && !f.confirmEmail)}>{busy ? t("Please wait…") : t("Disable my account")}</button><button type="button" className="btn secondary" onClick={() => setOpen(false)}>{t("Cancel")}</button></div>
        </form>
      )}
    </section>
  );
}
