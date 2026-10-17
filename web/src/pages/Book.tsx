import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, errorMessage, type BusySlot, type ProfessionalPage } from "../api";
import { useConfig, useLookup } from "../config";
import { fullName, when } from "../components";

import { t } from "../texts";
export default function Book() {
  const { id } = useParams();
  const proId = Number(id);
  const navigate = useNavigate();
  const [page, setPage] = useState<ProfessionalPage | null>(null);
  const [busy, setBusy] = useState<BusySlot[]>([]);
  const [professionId, setProfessionId] = useState<number | undefined>();
  const [start, setStart] = useState("");
  const cfg = useConfig();
  const { values: allDurations, name: durationName } = useLookup("duration_option");
  const durations = allDurations.filter(d => Number(d.code) >= cfg.num("Booking.MinDurationMinutes") && Number(d.code) <= cfg.num("Booking.MaxDurationMinutes"));
  const [duration, setDuration] = useState(0);
  useEffect(() => { if (durations.length && !durations.some(d => Number(d.code) === duration)) setDuration(Number((durations.find(d => d.code === "60") ?? durations[0]).code)); }, [durations, duration]);
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);

  useEffect(() => {
    api.professional(proId).then(p => { setPage(p); setProfessionId(p.professions[0]?.id); }).catch(e => setError(errorMessage(e)));
    api.busy(proId).then(setBusy).catch(() => undefined);
  }, [proId]);

  const startIso = useMemo(() => (start ? new Date(start).toISOString() : ""), [start]);
  const clash = useMemo(() => {
    if (!start) return false;
    const s = new Date(start).getTime(); const e = s + duration * 60000;
    return busy.some(b => new Date(b.startsAt).getTime() < e && new Date(b.endsAt).getTime() > s);
  }, [busy, start, duration]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null); setSending(true);
    try {
      await api.book({ professionalId: proId, professionId, startsAt: startIso, durationMinutes: duration, description: note.trim() || undefined });
      navigate("/appointments");
    } catch (err) { setError(errorMessage(err)); setSending(false); api.busy(proId).then(setBusy).catch(() => undefined); }
  }

  if (!page) return <main className="card"><p className="muted">{error ?? t("Loading…")}</p></main>;
  return (
    <main className="card narrow">
      <h1>{t("Book {fullName}", { fullName: fullName(page.profile.firstName, page.profile.lastName) })}</h1>
      <form onSubmit={submit} noValidate>
        {page.professions.length > 1 && (
          <label>{t("Service")}<select value={professionId ?? ""} onChange={e => setProfessionId(Number(e.target.value))}>{page.professions.map(p => <option key={p.id} value={p.id}>{p.serviceName}</option>)}</select></label>
        )}
        <label>{t("Date and time")}<input type="datetime-local" value={start} onChange={e => setStart(e.target.value)} required /></label>
        <label>{t("Duration")}<select value={duration} onChange={e => setDuration(Number(e.target.value))}>{durations.map(d => <option key={d.code} value={Number(d.code)}>{durationName(d)}</option>)}</select></label>
        <label>{t("What do you need? (optional)")}<input value={note} onChange={e => setNote(e.target.value)} maxLength={cfg.num("Booking.MaxDescriptionLength")} /></label>
        {clash && <p className="error" role="alert">{t("That time overlaps an existing booking.")}</p>}
        {error && <p className="error" role="alert">{error}</p>}
        <button className="btn" disabled={sending || !start || clash}>{sending ? t("Requesting…") : t("Request appointment")}</button>
        <p className="hint">{t("The professional must approve your request. Times are shown in your local time zone.")}</p>
      </form>
      {busy.length > 0 && (
        <>
          <h3>{t("Already booked")}</h3>
          <ul className="plain">{busy.slice(0, 12).map(b => <li key={b.startsAt} className="muted">{when(b.startsAt)} → {new Date(b.endsAt).toLocaleTimeString([], { timeStyle: "short" })}</li>)}</ul>
        </>
      )}
      <p><Link to={`/professional/${proId}`}>{t("Back to profile")}</Link></p>
    </main>
  );
}
