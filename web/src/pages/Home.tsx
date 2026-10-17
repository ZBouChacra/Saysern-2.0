import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, googleLinkUrl, type Listing } from "../api";
import { useAuth } from "../auth";
import { AdSlot } from "../AdSlot";
import { ListingCard } from "../components";
import { useConfig } from "../config";
import { t } from "../texts";

export default function Home() {
  const { user, loading } = useAuth();
  const site = useConfig().str("Site.Name") || "SaySerné";
  const navigate = useNavigate();
  const [google, setGoogle] = useState(false);
  const [top, setTop] = useState<Listing[]>([]);
  const [city, setCity] = useState("");
  const [params] = useSearchParams();
  useEffect(() => { api.providers().then(p => setGoogle(p.google)).catch(() => setGoogle(false)); }, []);
  useEffect(() => { api.topProfessionals(6).then(setTop).catch(() => setTop([])); }, []);

  const submit = (e: FormEvent) => { e.preventDefault(); navigate(city.trim() ? `/search?city=${encodeURIComponent(city.trim())}` : "/search"); };
  if (loading) return <p className="muted">{t("Loading…")}</p>;

  return (
    <main className="card wide">
      <AdSlot position="home_banner" />
      <h1>{t("Find the right professional for every need")}</h1>
      <form className="row hero" onSubmit={submit}>
        <input value={city} onChange={e => setCity(e.target.value)} placeholder={t("City (optional)")} aria-label={t("City")} />
        <button className="btn">{t("Find professionals")}</button>
        <Link className="btn secondary" to="/search">{t("Advanced search")}</Link>
      </form>

      {user && (
        <div className="panel">
          <strong>{t("Welcome, {firstName}", { firstName: user.firstName })}</strong>
          <span className="muted"> · {user.email} {user.emailVerified ? <span className="pill ok">{t("verified")}</span> : <span className="pill">{t("unverified")}</span>}</span>
          {params.get("google") === "already_linked" && <p className="error" role="alert">{t("That Google account is already linked to a different user.")}</p>}
          <div className="row">
            <Link className="btn small secondary" to="/me">{t("My profile")}</Link>
            <Link className="btn small secondary" to="/appointments">{t("Appointments")}</Link>
            {google && <a className="btn small secondary" href={googleLinkUrl("/")}>{t("Link a Google account")}</a>}
          </div>
        </div>
      )}

      <h2>{t("Top rated professionals")}</h2>
      {top.length === 0 ? <p className="muted">{t("No professionals listed yet. Be the first — create an account and add your services.")}</p>
        : <div className="list">{top.map(l => <ListingCard key={l.professionId} l={l} />)}</div>}
      {!user && <p className="row"><Link className="btn" to="/register">{t("Create an account")}</Link><Link className="btn secondary" to="/login">{t("Sign in")}</Link></p>}

      <section className="how" aria-label={t("How it works")}>
        <h2>{t("How it works")}</h2>
        <ol className="steps">
          <li><span className="step-no" aria-hidden="true">1</span><strong>{t("Search")}</strong><p>{t("Choose a service and a city, and compare professionals by rating, experience and price.")}</p></li>
          <li><span className="step-no" aria-hidden="true">2</span><strong>{t("Book")}</strong><p>{t("Ask for an appointment and chat with the professional before you decide.")}</p></li>
          <li><span className="step-no" aria-hidden="true">3</span><strong>{t("Review")}</strong><p>{t("After your appointment, share an honest review to help the next person.")}</p></li>
        </ol>
      </section>

      <section className="why" aria-label={t("Why people use {site}", { site })}>
        <h2>{t("Why people use {site}", { site })}</h2>
        <div className="why-grid">
          <div className="panel"><strong>{t("Real reviews")}</strong><p className="muted">{t("Only people who had an appointment can review, so ratings mean something.")}</p></div>
          <div className="panel"><strong>{t("Direct contact")}</strong><p className="muted">{t("Message the professional, share a photo or your location, and agree on the details.")}</p></div>
          <div className="panel"><strong>{t("In your language")}</strong><p className="muted">{t("The whole site, and every email we send, is available in English and Arabic.")}</p></div>
        </div>
      </section>

      {!user && (
        <section className="cta-pro" aria-label={t("Are you a professional?")}>
          <h2>{t("Are you a professional?")}</h2>
          <p>{t("List your services, receive booking requests and stand out with a premium listing.")}</p>
          <Link className="btn" to="/register">{t("Create an account")}</Link>
        </section>
      )}
    </main>
  );
}
