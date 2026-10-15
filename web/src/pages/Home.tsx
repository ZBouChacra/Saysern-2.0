import { useEffect, useState, type FormEvent } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { api, googleLinkUrl, type Listing } from "../api";
import { useAuth } from "../auth";
import { ListingCard } from "../components";

export default function Home() {
  const { user, loading } = useAuth();
  const navigate = useNavigate();
  const [google, setGoogle] = useState(false);
  const [top, setTop] = useState<Listing[]>([]);
  const [city, setCity] = useState("");
  const [params] = useSearchParams();
  useEffect(() => { api.providers().then(p => setGoogle(p.google)).catch(() => setGoogle(false)); }, []);
  useEffect(() => { api.topProfessionals(6).then(setTop).catch(() => setTop([])); }, []);

  const submit = (e: FormEvent) => { e.preventDefault(); navigate(city.trim() ? `/search?city=${encodeURIComponent(city.trim())}` : "/search"); };
  if (loading) return <p className="muted">Loading…</p>;

  return (
    <main className="card wide">
      <h1>Find the right professional for every need</h1>
      <form className="row hero" onSubmit={submit}>
        <input value={city} onChange={e => setCity(e.target.value)} placeholder="City (optional)" aria-label="City" />
        <button className="btn">Find professionals</button>
        <Link className="btn secondary" to="/search">Advanced search</Link>
      </form>

      {user && (
        <div className="panel">
          <strong>Welcome, {user.firstName}</strong>
          <span className="muted"> · {user.email} {user.emailVerified ? <span className="pill ok">verified</span> : <span className="pill">unverified</span>}</span>
          {params.get("google") === "already_linked" && <p className="error" role="alert">That Google account is already linked to a different user.</p>}
          <div className="row">
            <Link className="btn small secondary" to="/me">My profile</Link>
            <Link className="btn small secondary" to="/appointments">Appointments</Link>
            {google && <a className="btn small secondary" href={googleLinkUrl("/")}>Link a Google account</a>}
            {user.role === "admin" && <Link className="btn small" to="/admin">Back office</Link>}
          </div>
        </div>
      )}

      <h2>Top rated professionals</h2>
      {top.length === 0 ? <p className="muted">No professionals listed yet. Be the first — create an account and add your services.</p>
        : <div className="list">{top.map(l => <ListingCard key={l.professionId} l={l} />)}</div>}
      {!user && <p className="row"><Link className="btn" to="/register">Create an account</Link><Link className="btn secondary" to="/login">Sign in</Link></p>}
    </main>
  );
}
