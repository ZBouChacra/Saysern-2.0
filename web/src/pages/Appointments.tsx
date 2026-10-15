import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { api, errorMessage, type Appointment } from "../api";
import { useAuth } from "../auth";
import { fullName, when } from "../components";

export default function Appointments() {
  const { user } = useAuth();
  const [items, setItems] = useState<Appointment[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<number | null>(null);

  const load = useCallback(() => { api.appointments().then(setItems).catch(e => setError(errorMessage(e))); }, []);
  useEffect(load, [load]);

  async function act(id: number, status: "approved" | "cancelled" | "completed") {
    setBusyId(id); setError(null);
    try { await api.setStatus(id, status); load(); } catch (e) { setError(errorMessage(e)); } finally { setBusyId(null); }
  }

  if (!items) return <main className="card"><p className="muted">{error ?? "Loading…"}</p></main>;
  return (
    <main className="card wide">
      <h1>Appointments</h1>
      {error && <p className="error" role="alert">{error}</p>}
      {items.length === 0 && <p className="muted">Nothing booked yet. <Link to="/search">Find a professional</Link>.</p>}
      <div className="list">
        {items.map(a => {
          const asPro = a.professionalId === user?.id;
          const other = asPro ? fullName(a.clientFirstName, a.clientLastName) : fullName(a.professionalFirstName, a.professionalLastName);
          const started = new Date(a.startsAt).getTime() <= Date.now();
          return (
            <div className="panel" key={a.id}>
              <div className="row spread">
                <strong>{when(a.startsAt)} · {a.durationMinutes} min</strong>
                <span className={`pill ${a.status === "approved" || a.status === "completed" ? "ok" : a.status === "cancelled" ? "bad" : ""}`}>{a.status}</span>
              </div>
              <div className="muted">{asPro ? "Client" : "With"} {other}{a.serviceName ? ` · ${a.serviceName}` : ""}</div>
              {a.description && <p>{a.description}</p>}
              <div className="row">
                {asPro && a.status === "pending" && <button className="btn small" disabled={busyId === a.id} onClick={() => void act(a.id, "approved")}>Approve</button>}
                {asPro && a.status === "approved" && started && <button className="btn small" disabled={busyId === a.id} onClick={() => void act(a.id, "completed")}>Mark completed</button>}
                {(a.status === "pending" || a.status === "approved") && <button className="btn small secondary" disabled={busyId === a.id} onClick={() => void act(a.id, "cancelled")}>Cancel</button>}
                {!asPro && a.status === "completed" && !a.hasReview && <Link className="btn small secondary" to={`/professional/${a.professionalId}`}>Write a review</Link>}
                {!asPro && a.status === "completed" && a.hasReview && <span className="muted">Reviewed ✓</span>}
              </div>
            </div>
          );
        })}
      </div>
    </main>
  );
}
