import { useEffect, useState } from "react";
import { api, errorMessage, type NotificationPref } from "../api";
import { useLookup } from "../config";

/** Which emails the user gets. Events and channels come from lookups an admin maintains; a channel that is switched off is simply not offered. */
export default function Settings() {
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
    <main className="card wide">
      <h1>Notifications</h1>
      <p className="muted">Choose when we email you. Messages about your account itself (confirming your address, resetting your password) are always sent.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {prefs === null || events.loading || channels.loading ? <p className="muted">Loading…</p> : (
        <div className="table-wrap"><table className="t"><thead><tr><th>Email me when…</th>{channels.values.map(c => <th key={c.code}>{channels.name(c)}</th>)}</tr></thead><tbody>
          {events.values.map(ev => {
            const mandatory = ev.metadata?.mandatory === true;
            return (
              <tr key={ev.code}><td>{events.name(ev)}{mandatory && <span className="muted small"> (always sent)</span>}</td>
                {channels.values.map(c => <td key={c.code}>{mandatory ? <input type="checkbox" checked disabled aria-label={`${events.name(ev)} (${channels.name(c)}): always on`} />
                  : <input type="checkbox" checked={on(ev.code, c.code)} onChange={() => void toggle(ev.code, c.code)} aria-label={`${events.name(ev)} (${channels.name(c)})`} />}</td>)}
              </tr>
            );
          })}
        </tbody></table></div>
      )}
    </main>
  );
}
