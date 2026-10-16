import type { ReactNode } from "react";
import { Navigate, NavLink, Route, Routes } from "react-router-dom";
import { useSession } from "./session";
import Audit from "./pages/Audit";
import Catalog from "./pages/Catalog";
import Login from "./pages/Login";
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
  { to: "/audit", label: "Audit log", perm: "audit.read" },
  { to: "/security", label: "My security" },
];

function Guard({ perm, children }: { perm?: string; children: ReactNode }) {
  const { can } = useSession();
  return !perm || can(perm) ? <>{children}</> : <main className="card full"><h1>Not allowed</h1><p>Your role doesn't include this area.</p></main>;
}

function Home() {
  const { session, can } = useSession();
  const areas = NAV.filter(n => n.perm && can(n.perm));
  return (
    <main className="card full">
      <h1>Welcome, {session!.user.firstName}</h1>
      <p className="muted">{session!.isSuper ? "You are a super admin: every area is available." : `Your role gives you access to ${areas.length} area${areas.length === 1 ? "" : "s"}.`}</p>
      <p>{areas.map(a => a.label).join(" · ") || "Nothing assigned yet. Ask a super admin to give you a role."}</p>
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
            <Route path="/audit" element={<Guard perm="audit.read"><Audit /></Guard>} />
            <Route path="/security" element={<Security />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </div>
      </div>
    </>
  );
}
