import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, errorMessage, type Eligible, type ProfessionalPage, type ReviewItem } from "../api";
import { useAuth } from "../auth";
import { AdSlot } from "../AdSlot";
import { Avatar, Stars, fullName, money, useWeekdays, when } from "../components";
import { useConfig, useLookup } from "../config";

import { t } from "../texts";
export default function Professional() {
  const { id } = useParams();
  const userId = Number(id);
  const { user } = useAuth();
  const { dayName } = useWeekdays();
  const countries = useLookup("country");
  const zones = useLookup("time_zone");   // (hooks first: nothing may return before them)
  const cfg = useConfig();
  const navigate = useNavigate();
  const [page, setPage] = useState<ProfessionalPage | null>(null);
  const [reviews, setReviews] = useState<ReviewItem[]>([]);
  const [eligible, setEligible] = useState<Eligible[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [shared, setShared] = useState<string | null>(null);

  const load = () => {
    api.professional(userId).then(setPage).catch(e => (e?.status === 404 ? setNotFound(true) : setError(errorMessage(e))));
    api.reviews(userId).then(setReviews).catch(() => undefined);
    if (user) api.eligible(userId).then(setEligible).catch(() => setEligible([]));
  };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(load, [userId, user?.id]);

  async function message() {
    try { const room = await api.startChat(userId); navigate(`/chats/${room.id}`); } catch (e) { setError(errorMessage(e)); }
  }

  if (notFound) return <main className="card"><h1>{t("Professional not found")}</h1><Link to="/search">{t("Back to search")}</Link></main>;
  if (!page) return <main className="card"><p className="muted">{error ?? t("Loading…")}</p></main>;
  const { profile, professions, availability } = page;
  const own = user?.id === profile.id;

  /** Opens the phone's share sheet where there is one; otherwise copies the link (and as a last resort shows it to copy by hand). */
  async function share() {
    const url = `${window.location.origin}/professional/${userId}`;
    try {
      if (navigator.share) { await navigator.share({ title: fullName(profile.firstName, profile.lastName), url }); return; }
      await navigator.clipboard.writeText(url);
      setShared(t("Link copied."));
    } catch (e) {
      if (e instanceof DOMException && e.name === "AbortError") return;   // the person closed the share sheet
      window.prompt(t("Copy this link"), url);
    }
  }

  return (
    <main className="card wide">
      <div className="row spread">
        <div className="row">
          <Avatar photo={profile.profilePhoto} name={profile.firstName} big />
          <div>
            <h1>{fullName(profile.firstName, profile.lastName)}</h1>
            <div className="muted">{[profile.city, countries.label(profile.country)].filter(Boolean).join(", ")}{profile.age ? ` · ${t("{age} years old", { age: profile.age })}` : ""}</div>
            {profile.isPremium && <span className="pill ok">{t("premium")}</span>} {profile.isStarred && <span className="pill">{t("★ featured")}</span>}
            <div><button className="link" onClick={() => void share()}>{t("Share this profile")}</button>{shared && <span className="ok-text" role="status"> {shared}</span>}</div>
          </div>
        </div>
        {!own && (
          <div className="row">
            {user ? (
              <>
                <Link className="btn" to={`/book/${profile.id}`}>{t("Book an appointment")}</Link>
                <button className="btn secondary" onClick={() => void message()}>{t("Message")}</button>
              </>
            ) : <Link className="btn" to="/login">{t("Sign in to book")}</Link>}
          </div>
        )}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {profile.bio && <p>{profile.bio}</p>}

      <h2>{t("Services")}</h2>
      <div className="list">
        {professions.map(p => (
          <div className="panel" key={p.id}>
            <div className="row spread"><strong>{p.serviceName}</strong><Stars value={p.avgRating} count={p.totalReviews} /></div>
            <div className="muted">{t("{categoryName} · {money} · {yearsOfExperience} yrs experience", { categoryName: p.categoryName, money: money(p.costPerHour), yearsOfExperience: p.yearsOfExperience })}{p.hasTeam ? ` · ${t("team of {teamSize}", { teamSize: p.teamSize ?? "" })}` : ""}</div>
            {p.hasOffice && <div className="muted">{t("Office: {join}", { join: [p.officeAddress, p.officeCity, p.officeCountry].filter(Boolean).join(", ") })}</div>}
            {p.geographicAreas.length > 0 && <div className="muted">{t("Serves: {join}", { join: p.geographicAreas.join(", ") })}</div>}
            {p.website && <a href={p.website} target="_blank" rel="noopener noreferrer">{t("Website")}</a>}
            <div className="muted small">
              {availability.filter(a => a.professionId === p.id || a.professionId === undefined).length === 0 ? t("No availability listed.") :
                <>{availability.filter(a => a.professionId === p.id || a.professionId === undefined).map(a => `${dayName(a.dayOfWeek).slice(0, 3)} ${a.startTime}–${a.endTime}`).join(" · ")}
                  {" "}<span>({t("Hours are in {zone} time.", { zone: zones.label(profile.timeZone ?? cfg.str("Site.DefaultTimeZone")) })})</span></>}
            </div>
          </div>
        ))}
        {professions.length === 0 && <p className="muted">{t("No services listed.")}</p>}
      </div>

      {user && eligible.length > 0 && <ReviewForm appointmentId={eligible[0].id} when={when(eligible[0].startsAt)} onDone={load} />}

      <h2>{t("Reviews")}</h2>
      {reviews.length === 0 && <p className="muted">{t("No reviews yet.")}</p>}
      {reviews.map(r => (
        <div className="panel" key={r.id}>
          <div className="row spread"><strong>{fullName(r.reviewerFirstName, r.reviewerLastName)}</strong><Stars value={r.rating} /></div>
          {r.comment && <p>{r.comment}</p>}
          <div className="muted small">{when(r.createdAt)}</div>
          <ReviewReply review={r} ownerName={profile.firstName} mine={user?.id === userId} onSaved={updated => setReviews(list => list.map(x => (x.id === updated.id ? { ...x, ...updated } : x)))} />
        </div>
      ))}
      <AdSlot position="sidebar" />
    </main>
  );
}

/** The professional's answer to a review: shown to everyone, written (and edited) only by the professional it is about. */
function ReviewReply({ review, ownerName, mine, onSaved }: { review: ReviewItem; ownerName: string; mine: boolean; onSaved: (r: ReviewItem) => void }) {
  const cfg = useConfig();
  const max = cfg.num("Reviews.MaxReplyLength") || 1000;
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState(review.replyText ?? "");
  const [error, setError] = useState<string | null>(null);
  async function save() {
    try { onSaved(await api.replyToReview(review.id, text.trim())); setEditing(false); setError(null); } catch (e) { setError(errorMessage(e)); }
  }
  return (
    <div className="review-reply">
      {review.replyText && !editing && <div className="reply-box"><strong>{t("Reply from {ownerName}", { ownerName })}</strong>{review.replyEditedAt && <span className="muted small">{" "}{t("(edited)")}</span>}<p>{review.replyText}</p></div>}
      {mine && !editing && <button className="link" onClick={() => { setText(review.replyText ?? ""); setEditing(true); }}>{review.replyText ? t("Edit your reply") : t("Reply")}</button>}
      {mine && editing && (
        <div>
          <textarea aria-label={t("Your reply")} rows={3} maxLength={max} value={text} onChange={e => setText(e.target.value)} />
          <div className="muted small">{text.length} / {max}</div>
          {error && <p className="error" role="alert">{error}</p>}
          <div className="row"><button className="btn small" disabled={!text.trim()} onClick={() => void save()}>{t("Save reply")}</button><button className="btn small secondary" onClick={() => setEditing(false)}>{t("Cancel")}</button></div>
        </div>
      )}
    </div>
  );
}

function ReviewForm({ appointmentId, when: label, onDone }: { appointmentId: number; when: string; onDone: () => void }) {
  const [rating, setRating] = useState(5);
  const [comment, setComment] = useState("");
  const [error, setError] = useState<string | null>(null);
  async function submit(e: FormEvent) {
    e.preventDefault();
    try { await api.review(appointmentId, rating, comment.trim() || undefined); setComment(""); onDone(); } catch (err) { setError(errorMessage(err)); }
  }
  return (
    <form className="panel" onSubmit={submit}>
      <h3>{t("Review your appointment on {label}", { label })}</h3>
      <label>{t("Rating")}<select value={rating} onChange={e => setRating(Number(e.target.value))}>{[5, 4, 3, 2, 1].map(n => <option key={n} value={n}>{"★".repeat(n)}</option>)}</select></label>
      <label>{t("Comment")}<input value={comment} onChange={e => setComment(e.target.value)} maxLength={2000} /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="btn">{t("Submit review")}</button>
    </form>
  );
}
