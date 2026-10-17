import { useEffect, useState, type ReactNode } from "react";
import { Navigate, NavLink, Route, Routes } from "react-router-dom";
import { api, errorMessage, type Dashboard, type DashboardYear } from "./api";
import { useLanguage } from "./language";
import { useSession } from "./session";
import Audit from "./pages/Audit";
import Catalog from "./pages/Catalog";
import Login from "./pages/Login";
import AdPrices from "./pages/AdPrices";
import Ads from "./pages/Ads";
import Jobs from "./pages/Jobs";
import Brand from "./pages/Brand";
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

import { formatWhen } from "./time";
import { t } from "./texts";
const NAV: { to: string; label: () => string; perm?: string }[] = [
  { to: "/users", label: () => t("Users"), perm: "users.read" },
  { to: "/roles", label: () => t("Roles"), perm: "roles.manage" },
  { to: "/settings", label: () => t("Settings"), perm: "settings.read" },
  { to: "/brand", label: () => t("Brand"), perm: "settings.read" },
  { to: "/lookups", label: () => t("Lookups"), perm: "lookups.manage" },
  { to: "/translations", label: () => t("Texts"), perm: "translations.manage" },
  { to: "/catalog", label: () => t("Catalog"), perm: "catalog.manage" },
  { to: "/contact", label: () => t("Contact messages"), perm: "contact.read" },
  { to: "/reviews", label: () => t("Reviews"), perm: "reviews.read" },
  { to: "/chats", label: () => t("Conversations"), perm: "chats.read" },
  { to: "/payments", label: () => t("Payments"), perm: "payments.read" },
  { to: "/premium", label: () => t("Premium"), perm: "premium.read" },
  { to: "/ads", label: () => t("Ads"), perm: "ads.read" },
  { to: "/ad-prices", label: () => t("Ad prices"), perm: "ads.read" },
  { to: "/jobs", label: () => t("Background jobs"), perm: "jobs.read" },
  { to: "/outbox", label: () => t("Email log"), perm: "email.read" },
  { to: "/audit", label: () => t("Audit log"), perm: "audit.read" },
  { to: "/security", label: () => t("My security") },
];

function Guard({ perm, children }: { perm?: string; children: ReactNode }) {
  const { can } = useSession();
  return !perm || can(perm) ? <>{children}</> : <main className="card full"><h1>{t("Not allowed")}</h1><p>{t("Your role doesn't include this area.")}</p></main>;
}

function Tile({ to, label, value, hint, alert }: { to?: string; label: string; value: string | number; hint?: string; alert?: boolean }) {
  const body = <><div className="tile-value">{value}</div><div className="tile-label">{label}</div>{hint && <div className="muted small">{hint}</div>}</>;
  return to ? <NavLink to={to} className={`tile ${alert ? "alert" : ""}`}>{body}</NavLink> : <div className={`tile ${alert ? "alert" : ""}`}>{body}</div>;
}

/** The columns of the month-by-month table, named in the current language. */
const columnName = (key: string): string => {
  switch (key) {
    case "newUsers": return t("New users"); case "appointments": return t("Appointments requested"); case "reviews": return t("Reviews");
    case "contactMessages": return t("Contact messages"); case "receipts": return t("Receipts issued"); case "received": return t("Amount received (USD)"); case "voided": return t("Receipts voided");
    default: return key;
  }
};

/** Month by month for one calendar year, with a spreadsheet to download. Each column only for staff who may see that area. */
function YearTable() {
  const [data, setData] = useState<DashboardYear | null>(null);
  const [year, setYear] = useState<number | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { api.dashboardYear(year).then(d => { setData(d); setError(null); }).catch(e => setError(errorMessage(e))); }, [year]);
  if (error) return <p className="error" role="alert">{error}</p>;
  if (!data || data.columns.length === 0) return null;
  const monthName = (m: number) => new Date(2000, m - 1, 1).toLocaleString(document.documentElement.lang === "ar" ? "ar-u-nu-latn" : document.documentElement.lang || "en", { month: "long" });
  return (
    <section aria-label={t("Month by month")} data-testid="year-table">
      <h2>{t("Month by month")}</h2>
      <div className="toolbar">
        <label>{t("Year")}<select value={data.year} onChange={e => setYear(Number(e.target.value))}>{data.years.map(y => <option key={y} value={y}>{y}</option>)}</select></label>
        <a className="btn secondary" href={`/api/admin/dashboard/year.csv?year=${data.year}`} download>{t("Download as a spreadsheet (CSV)")}</a>
      </div>
      <div className="table-wrap"><table className="t"><thead><tr><th>{t("Month")}</th>{data.columns.map(c => <th key={c}>{columnName(c)}</th>)}</tr></thead><tbody>
        {data.months.map(m => <tr key={m.month}><td>{monthName(m.month)}</td>{data.columns.map(c => <td key={c}>{c === "received" ? m[c].toFixed(2) : m[c]}</td>)}</tr>)}
      </tbody></table></div>
    </section>
  );
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
      <h1>{t("Welcome, {firstName}", { firstName: session!.user.firstName })}</h1>
      <p className="muted">{session!.isSuper ? t("You are a super admin: every area is available.") : `Your role gives you access to ${areas.length} area${areas.length === 1 ? "" : "s"}.`}</p>
      {error && <p className="error" role="alert">{error}</p>}
      {areas.length === 0 && <p>{t("Nothing assigned yet. Ask a super admin to give you a role.")}</p>}
      {d && (
        <div className="dashboard" data-testid="dashboard">
          {d.contact && <Tile to="/contact" label={t("Unanswered messages")} value={d.contact.unanswered} hint={t("{total} in total", { total: d.contact.total })} alert={d.contact.unanswered > 0} />}
          {d.users && <Tile to="/users" label={t("Users")} value={d.users.total} hint={t("{week} new this week · {professionals} professionals", { week: d.users.last7Days, professionals: d.users.professionals })} />}
          {d.users && <Tile label={t("Appointments (30 days)")} value={d.users.appointments.pending + d.users.appointments.approved + d.users.appointments.completed + d.users.appointments.cancelled} hint={t("{waiting} waiting · {completed} completed", { waiting: d.users.appointments.pending, completed: d.users.appointments.completed })} />}
          {d.reviews && <Tile to="/reviews" label={t("Reviews")} value={d.reviews.total} hint={t("average {average} · {hidden} hidden", { average: d.reviews.average.toFixed(1), hidden: d.reviews.hidden })} />}
          {d.ads && <Tile to="/ads" label={t("Ads running today")} value={d.ads.runningToday} hint={t("{count} bookings waiting for payment", { count: d.ads.pendingBatches })} />}
          {d.premium && <Tile to="/premium" label={t("Premium today")} value={d.premium.activeToday} hint={t("{count} bookings waiting for payment", { count: d.premium.pendingBatches })} />}
          {d.payments && <Tile to="/payments" label={t("Received this month")} value={money(d.payments.receivedThisMonth, d.payments.currency)} hint={t("{amount} in 30 days · {receipts} receipts", { amount: money(d.payments.receivedLast30Days, d.payments.currency), receipts: d.payments.receiptsLast30Days })} />}
          {d.system && (d.system.failedEmails !== undefined || d.system.failedJobs !== undefined) && <Tile to={d.system.failedJobs !== undefined ? "/jobs" : "/outbox"} label={t("Failures to look at")} value={(d.system.failedJobs ?? 0) + (d.system.failedEmails ?? 0)} hint={t("failed jobs and emails")} alert={(d.system.failedJobs ?? 0) + (d.system.failedEmails ?? 0) > 0} />}
        </div>
      )}
      <YearTable />
      {d?.recentActivity && d.recentActivity.length > 0 && (
        <section aria-label={t("Recent activity")}><h2>{t("Recent activity")}</h2>
          <ul className="plain">{d.recentActivity.map(a => <li key={a.id}><span className="muted small">{formatWhen(a.occurredAt)}</span> {a.actorName ?? t("System")}: {a.summary}</li>)}</ul>
        </section>
      )}
    </main>
  );
}

function LanguageSwitch() {
  const { lang, setLang, languages } = useLanguage();
  if (languages.length < 2) return null;
  return <select aria-label={t("Language")} value={lang} onChange={e => setLang(e.target.value)}>{languages.map(l => <option key={l.code} value={l.code}>{l.name}</option>)}</select>;
}

export default function App() {
  const { session, loading, can, signOut } = useSession();
  if (loading) return <p className="muted" style={{ padding: "2rem" }}>{t("Loading…")}</p>;
  if (!session) return <Routes><Route path="*" element={<Login />} /></Routes>;
  return (
    <>
      <header className="topbar">
        <span className="brand">Say<span>Serné</span> <span className="muted" style={{ fontSize: ".9rem" }}>{t("Back office")}</span></span>
        <nav><LanguageSwitch /><span className="who">{session.user.firstName} {session.user.lastName}</span><button className="link" onClick={() => void signOut()}>{t("Sign out")}</button></nav>
      </header>
      <div className="shell">
        <aside className="side" aria-label={t("Sections")}>{NAV.filter(n => !n.perm || can(n.perm)).map(n => <NavLink key={n.to} to={n.to} className={({ isActive }) => (isActive ? "active" : "")}>{n.label()}</NavLink>)}</aside>
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
            <Route path="/brand" element={<Guard perm="settings.read"><Brand /></Guard>} />
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
