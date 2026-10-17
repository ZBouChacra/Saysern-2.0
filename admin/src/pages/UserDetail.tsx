import { useEffect, useState } from "react";
import { api, errorMessage, type UserDetail as Detail } from "../api";
import { statusLabel } from "../i18n";
import { t } from "../texts";
import { formatWhen } from "../time";

const weekday = (dow: number) => new Date(2021, 0, 3 + dow).toLocaleDateString(document.documentElement.lang === "ar" ? "ar-u-nu-latn" : document.documentElement.lang || "en", { weekday: "long" });

/** Everything about one person, for support. Opening it is recorded in the audit log. */
export default function UserDetail({ id, onClose }: { id: number; onClose: () => void }) {
  const [d, setD] = useState<Detail | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { setD(null); api.userDetail(id).then(x => { setD(x); setError(null); }).catch(e => setError(errorMessage(e))); }, [id]);

  return (
    <section className="card" aria-label={t("User details")} data-testid="user-detail">
      <div className="row spread"><h2>{d ? `${d.user.firstName} ${d.user.lastName}` : t("User details")}</h2><button className="btn small secondary" onClick={onClose}>{t("Close")}</button></div>
      <p className="muted small">{t("Opening this page is recorded in the audit log.")}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {!d && !error && <p className="muted">{t("Loading…")}</p>}
      {d && (<>
        <p>
          <b>{d.user.email}</b>{" "}
          <span className={`pill ${d.user.emailVerified ? "ok" : ""}`}>{d.user.emailVerified ? t("verified") : t("unverified")}</span>
          {d.user.isLocked && <span className="pill bad"> {t("locked")}</span>}
          {d.user.isDisabled && <span className="pill bad"> {t("disabled by the user")}</span>}
          {d.user.isStarred && <span className="pill"> {t("★ featured")}</span>}
        </p>
        <p className="muted">{t("{role} · {type} · joined {when}", { role: d.user.role, type: d.user.profileType, when: formatWhen(d.user.createdAtUtc) })}{d.user.timeZone ? ` · ${d.user.timeZone}` : ""}{d.user.preferredLanguage ? ` · ${d.user.preferredLanguage}` : ""}</p>

        {d.profile && (
          <table className="t"><tbody>
            <tr><th>{t("Phone")}</th><td>{d.profile.phone ?? "—"}</td><th>{t("Date of birth")}</th><td>{d.profile.dateOfBirth ?? "—"}</td></tr>
            <tr><th>{t("Sex")}</th><td>{d.profile.sex ?? "—"}</td><th>{t("Nationality")}</th><td>{d.profile.nationality ?? "—"}</td></tr>
            <tr><th>{t("Country")}</th><td>{d.profile.country ?? "—"}</td><th>{t("City")}</th><td>{d.profile.city ?? "—"}</td></tr>
          </tbody></table>
        )}
        {d.profile?.bio && <p className="pre">{d.profile.bio}</p>}

        <h3>{t("Listings")}</h3>
        {d.professions.length === 0 ? <p className="muted">{t("No listings.")}</p> : (
          <ul className="plain">{d.professions.map(p => (
            <li key={p.id}><b>{p.categoryName} · {p.serviceName}</b> {p.isHidden && <span className="pill">{t("hidden")}</span>}
              <div className="muted small">{[p.city, p.country].filter(Boolean).join(", ")} · {p.costPerHour ?? "—"} USD/h · {t("{years} years of experience", { years: p.yearsOfExperience })}</div>
              <div className="muted small">{t("Hours")}: {d.availability.filter(a => a.professionId == null || a.professionId === p.id).map(a => `${weekday(a.dayOfWeek)} ${a.startTime}–${a.endTime}`).join(" · ") || t("none set (always open)")}</div>
            </li>))}</ul>
        )}

        <h3>{t("Recent appointments")}</h3>
        {d.appointments.length === 0 ? <p className="muted">{t("No appointments.")}</p> : (
          <div className="table-wrap"><table className="t"><thead><tr><th>{t("When")}</th><th>{t("Client")}</th><th>{t("Professional")}</th><th>{t("Status")}</th></tr></thead><tbody>
            {d.appointments.map(a => <tr key={a.id}><td>{formatWhen(a.startsAt)} ({t("{minutes} min", { minutes: a.durationMinutes })})</td><td>{a.clientFirstName} {a.clientLastName}</td><td>{a.professionalFirstName} {a.professionalLastName}</td><td><span className="pill">{statusLabel(a.status)}</span></td></tr>)}
          </tbody></table></div>
        )}

        <p className="muted">{t("As a client: {asClient} appointments · as a professional: {asProfessional} · reviews written: {written} · reviews received: {received} ({hidden} hidden) · conversations: {conversations} · premium bookings: {premium}", {
          asClient: d.counts.appointmentsAsClient, asProfessional: d.counts.appointmentsAsProfessional, written: d.counts.reviewsWritten, received: d.counts.reviewsReceived, hidden: d.counts.reviewsHidden, conversations: d.counts.conversations, premium: d.counts.premiumBatches })}</p>
      </>)}
    </section>
  );
}
