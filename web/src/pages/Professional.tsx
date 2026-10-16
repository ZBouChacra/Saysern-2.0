import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, errorMessage, type Eligible, type ProfessionalPage, type ReviewItem } from "../api";
import { useAuth } from "../auth";
import { Avatar, Stars, fullName, money, useWeekdays, when } from "../components";
import { useLookup } from "../config";

export default function Professional() {
  const { id } = useParams();
  const userId = Number(id);
  const { user } = useAuth();
  const { dayName } = useWeekdays();
  const countries = useLookup("country");
  const navigate = useNavigate();
  const [page, setPage] = useState<ProfessionalPage | null>(null);
  const [reviews, setReviews] = useState<ReviewItem[]>([]);
  const [eligible, setEligible] = useState<Eligible[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);

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

  if (notFound) return <main className="card"><h1>Professional not found</h1><Link to="/search">Back to search</Link></main>;
  if (!page) return <main className="card"><p className="muted">{error ?? "Loading…"}</p></main>;
  const { profile, professions, availability } = page;
  const own = user?.id === profile.id;

  return (
    <main className="card wide">
      <div className="row spread">
        <div className="row">
          <Avatar photo={profile.profilePhoto} name={profile.firstName} big />
          <div>
            <h1>{fullName(profile.firstName, profile.lastName)}</h1>
            <div className="muted">{[profile.city, countries.label(profile.country)].filter(Boolean).join(", ")}{profile.age ? ` · ${profile.age} yrs` : ""}</div>
            {profile.isPremium && <span className="pill ok">premium</span>} {profile.isStarred && <span className="pill">★ featured</span>}
          </div>
        </div>
        {!own && (
          <div className="row">
            {user ? (
              <>
                <Link className="btn" to={`/book/${profile.id}`}>Book an appointment</Link>
                <button className="btn secondary" onClick={() => void message()}>Message</button>
              </>
            ) : <Link className="btn" to="/login">Sign in to book</Link>}
          </div>
        )}
      </div>
      {error && <p className="error" role="alert">{error}</p>}
      {profile.bio && <p>{profile.bio}</p>}

      <h2>Services</h2>
      <div className="list">
        {professions.map(p => (
          <div className="panel" key={p.id}>
            <div className="row spread"><strong>{p.serviceName}</strong><Stars value={p.avgRating} count={p.totalReviews} /></div>
            <div className="muted">{p.categoryName} · {money(p.costPerHour)} · {p.yearsOfExperience} yrs experience{p.hasTeam ? ` · team of ${p.teamSize}` : ""}</div>
            {p.hasOffice && <div className="muted">Office: {[p.officeAddress, p.officeCity, p.officeCountry].filter(Boolean).join(", ")}</div>}
            {p.geographicAreas.length > 0 && <div className="muted">Serves: {p.geographicAreas.join(", ")}</div>}
            {p.website && <a href={p.website} target="_blank" rel="noopener noreferrer">Website</a>}
            <div className="muted small">
              {availability.filter(a => a.professionId === p.id || a.professionId === undefined).length === 0 ? "No availability listed." :
                availability.filter(a => a.professionId === p.id || a.professionId === undefined).map(a => `${dayName(a.dayOfWeek).slice(0, 3)} ${a.startTime}–${a.endTime}`).join(" · ")}
            </div>
          </div>
        ))}
        {professions.length === 0 && <p className="muted">No services listed.</p>}
      </div>

      {user && eligible.length > 0 && <ReviewForm appointmentId={eligible[0].id} when={when(eligible[0].startsAt)} onDone={load} />}

      <h2>Reviews</h2>
      {reviews.length === 0 && <p className="muted">No reviews yet.</p>}
      {reviews.map(r => (
        <div className="panel" key={r.id}>
          <div className="row spread"><strong>{fullName(r.reviewerFirstName, r.reviewerLastName)}</strong><Stars value={r.rating} /></div>
          {r.comment && <p>{r.comment}</p>}
          <div className="muted small">{when(r.createdAt)}</div>
        </div>
      ))}
    </main>
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
      <h3>Review your appointment on {label}</h3>
      <label>Rating
        <select value={rating} onChange={e => setRating(Number(e.target.value))}>{[5, 4, 3, 2, 1].map(n => <option key={n} value={n}>{"★".repeat(n)}</option>)}</select></label>
      <label>Comment<input value={comment} onChange={e => setComment(e.target.value)} maxLength={2000} /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="btn">Submit review</button>
    </form>
  );
}
