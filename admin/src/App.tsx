import { useEffect, useState, type ReactNode } from "react";
import { Navigate, NavLink, Route, Routes } from "react-router-dom";
import { api, errorMessage, type Dashboard } from "./api";
import { useSession } from "./session";
import Audit from "./pages/Audit";
import Catalog from "./pages/Catalog";
import Login from "./pages/Login";
import AdPrices from "./pages/AdPrices";
import Ads from "./pages/Ads";
import Jobs from "./pages/Jobs";
import Chats from "./pages/Chats";
import Contact from "./pages/Contact";
import Outbox from "./pages/Outbox";
import Reviews from "./pages/Reviews";
import Payments from "./pages/Payments";
import ReceiptPage from "./pages/ReceiptPage";
import Premium from "./pages/Premium";
import Lookups from "./pages/Lookups";
import Roles from "./pages/Roles";
import Security from "./pages/Security";
import Settings from "./pages/Settings";
import Translations from "./pages/Translations";
import Users from "./pages/Users";

const NAV: { to: string; label: string; perm?: string }[] = [
  { to: "/users", label: "Users", perm: "users.read" },
  { to: "/roles", label: "Roles", perm: "roles.manage" },
  { to: "/settings", label: "Settings", perm: "settings.read" },
  { to: "/lookups", label: "Lookups", perm: "lookups.manage" },
  { to: "/translations", label: "Texts", perm: "translations.manage" },
  { to: "/catalog", label: "Catalog", perm: "catalog.manage" },
  { to: "/contact", label: "Contact messages", perm: "contact.read" },
  { to: "/reviews", label: "Reviews", perm: "reviews.read" },
  { to: "/chats", label: "Conversations", perm: "chats.read" },
  { to: "/payments", label: "Payments", perm: "payments.read" },
  { to: "/premium", label: "Premium", perm: "premium.read" },
  { to: "/ads", label: "Ads", perm: "ads.read" },
  { to: "/ad-prices", label: "Ad prices", perm: "ads.read" },
  { to: "/jobs", label: "Background jobs", perm: "jobs.read" },
  { to: "/outbox", label: "Email log", perm: "email.read" },
  { to: "/audit", label: "Audit log", perm: "audit.read" },
  { to: "/security", label: "My security" },
];

function Guard({ perm, children }: { perm?: string; children: ReactNode }) {
  const { can } = useSession();
  return !perm || can(perm) ? <>{children}</> : <main className="card full"><h1>Not allowed</h1><p>Your role doesn't include this area.</p></main>;
}

function Tile({ to, label, value, hint, alert }: { to?: string; label: string; value: string | number; hint?: string; alert?: boolean }) {
  const body = <><div className="tile-value">{value}</div><div className="tile-label">{label}</div>{hint && <div className="muted small">{hint}</div>}</>;
  return to ? <NavLink to={to} className={`tile ${alert ? "alert" : ""}`}>{body}</NavLink> : <div className={`tile ${alert ? "alert" : ""}`}>{body}</div>;
}

function Home() {
  const { session, can } = useSession();
  const areas = NAV.filter(n => n.perm && can(n.perm));
  const [d, setD] = useState<Dashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.dashboard().then(setD).catch(e => setError(errorMessage(e))); }, []);
  const money = (n: number, c: string) => `${n.toFixed(2)} ${c}`;
  return (
    <main className="card full">
      <h1>Welcome, {session!.user.firstName}</h1>
      <p className="muted">{session!.isSuper ? "You are a super admin: every area is available." : `Your role gives you access to ${areas.length} area${areas.length === 1 ? "" : "s"}.`}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {areas.length === 0 && <p>Nothing assigned yet. Ask a super admin to give you a role.</p>}
      {d && (
        <div className="dashboard" data-testid="dashboard">
          {d.contact && <Tile to="/contact" label="Unanswered messages" value={d.contact.unanswered} hint={`${d.contact.total} in total`} alert={d.contact.unanswered > 0} />}
          {d.users && <Tile to="/users" label="Users" value={d.users.total} hint={`${d.users.last7Days} new this week · ${d.users.professionals} professionals`} />}
          {d.users && <Tile label="Appointments (30 days)" value={d.users.appointments.pending + d.users.appointments.approved + d.users.appointments.completed + d.users.appointments.cancelled} hint={`${d.users.appointments.pending} waiting · ${d.users.appointments.completed} completed`} />}
          {d.reviews && <Tile to="/reviews" label="Reviews" value={d.reviews.total} hint={`average ${d.reviews.average.toFixed(1)} · ${d.reviews.hidden} hidden`} />}
          {d.ads && <Tile to="/ads" label="Ads running today" value={d.ads.runningToday} hint={`${d.ads.pendingBatches} bookings waiting for payment`} />}
          {d.premium && <Tile to="/premium" label="Premium today" value={d.premium.activeToday} hint={`${d.premium.pendingBatches} bookings waiting for payment`} />}
          {d.payments && <Tile to="/payments" label="Received this month" value={money(d.payments.receivedThisMonth, d.payments.currency)} hint={`${money(d.payments.receivedLast30Days, d.payments.currency)} in 30 days · ${d.payments.receiptsLast30Days} receipts`} />}
          {d.system && (d.system.failedEmails !== undefined || d.system.failedJobs !== undefined) && <Tile to={d.system.failedJobs !== undefined ? "/jobs" : "/outbox"} label="Failures to look at" value={(d.system.failedJobs ?? 0) + (d.system.failedEmails ?? 0)} hint="failed jobs and emails" alert={(d.system.failedJobs ?? 0) + (d.system.failedEmails ?? 0) > 0} />}
        </div>
      )}
      {d?.recentActivity && d.recentActivity.length > 0 && (
        <section aria-label="Recent activity"><h2>Recent activity</h2>
          <ul className="plain">{d.recentActivity.map(a => <li key={a.id}><span className="muted small">{new Date(a.occurredAt).toLocaleString()}</span> {a.actorName ?? "System"}: {a.summary}</li>)}</ul>
        </section>
      )}
    </main>
  );
}

export default function App() {
  const { session, loading, can, signOut } = useSession();
  if (loading) return <p className="muted" style={{ padding: "2rem" }}>Loading…</p>;
  if (!session) return <Routes><Route path="*" element={<Login />} /></Routes>;
  return (
    <>
      <header className="topbar">
        <span className="brand">Say<span>Serné</span> <span className="muted" style={{ fontSize: ".9rem" }}>Back office</span></span>
        <nav><span className="who">{session.user.firstName} {session.user.lastName}</span><button className="link" onClick={() => void signOut()}>Sign out</button></nav>
      </header>
      <div className="shell">
        <aside className="side" aria-label="Sections">{NAV.filter(n => !n.perm || can(n.perm)).map(n => <NavLink key={n.to} to={n.to} className={({ isActive }) => (isActive ? "active" : "")}>{n.label}</NavLink>)}</aside>
        <div className="content">
          <Routes>
            <Route path="/" element={<Home />} />
            <Route path="/users" element={<Guard perm="users.read"><Users /></Guard>} />
            <Route path="/roles" element={<Guard perm="roles.manage"><Roles /></Guard>} />
            <Route path="/settings" element={<Guard perm="settings.read"><Settings /></Guard>} />
            <Route path="/lookups" element={<Guard perm="lookups.manage"><Lookups /></Guard>} />
            <Route path="/translations" element={<Guard perm="translations.manage"><Translations /></Guard>} />
            <Route path="/catalog" element={<Guard perm="catalog.manage"><Catalog /></Guard>} />
            <Route path="/outbox" element={<Guard perm="email.read"><Outbox /></Guard>} />
            <Route path="/contact" element={<Guard perm="contact.read"><Contact /></Guard>} />
            <Route path="/reviews" element={<Guard perm="reviews.read"><Reviews /></Guard>} />
            <Route path="/chats" element={<Guard perm="chats.read"><Chats /></Guard>} />
            <Route path="/payments" element={<Guard perm="payments.read"><Payments /></Guard>} />
            <Route path="/payments/:id" element={<Guard perm="payments.read"><ReceiptPage /></Guard>} />
            <Route path="/premium" element={<Guard perm="premium.read"><Premium /></Guard>} />
            <Route path="/ads" element={<Guard perm="ads.read"><Ads /></Guard>} />
            <Route path="/ad-prices" element={<Guard perm="ads.read"><AdPrices /></Guard>} />
            <Route path="/jobs" element={<Guard perm="jobs.read"><Jobs /></Guard>} />
            <Route path="/audit" element={<Guard perm="audit.read"><Audit /></Guard>} />
            <Route path="/security" element={<Security />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </div>
      </div>
    </>
  );
}
