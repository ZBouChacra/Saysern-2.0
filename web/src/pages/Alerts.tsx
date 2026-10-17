import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage, toQuery, type Alert } from "../api";
import { useLookup } from "../config";

import { t } from "../texts";
import { tx } from "../i18n";
const describe = (c: Alert["searchCriteria"]) =>
  Object.entries(c).filter(([, v]) => v !== undefined && v !== null && v !== "").map(([k, v]) => `${k}: ${v}`).join(" · ") || "everything";

export default function Alerts() {
  const frequencies = useLookup("alert_frequency");
  const [alerts, setAlerts] = useState<Alert[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => { api.alerts().then(setAlerts).catch(e => setError(errorMessage(e))); }, []);
  useEffect(load, [load]);

  async function remove(id: number) {
    try { await api.deleteAlert(id); load(); } catch (e) { setError(errorMessage(e)); }
  }
  if (!alerts) return <main className="card"><p className="muted">{error ?? t("Loading…")}</p></main>;
  return (
    <main className="card">
      <h1>{t("Saved searches")}</h1>
      <p className="muted">{tx("Save a search from the {link}. Re-run it any time to see who matches now.", { link: <Link to="/search">{t("search page")}</Link> })}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {alerts.length === 0 && <p className="muted">{t("You have no saved searches.")}</p>}
      {alerts.map(a => (
        <div className="panel" key={a.id}>
          <div className="row spread"><strong>{a.name}</strong><span className="pill">{frequencies.label(a.frequency)}</span></div>
          <div className="muted small">{describe(a.searchCriteria)}</div>
          <div className="row">
            <Link className="btn small" to={`/search?${toQuery(a.searchCriteria)}`}>{t("Run now")}</Link>
            <button className="btn small secondary" onClick={() => void remove(a.id)}>{t("Delete")}</button>
          </div>
        </div>
      ))}
    </main>
  );
}
