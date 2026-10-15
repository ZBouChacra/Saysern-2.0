import { Link, Navigate, Route, Routes } from "react-router-dom";
import { useEffect, useState, type ReactNode } from "react";
import { api } from "./api";
import { useAuth } from "./auth";
import Admin from "./pages/Admin";
import Alerts from "./pages/Alerts";
import Appointments from "./pages/Appointments";
import Book from "./pages/Book";
import Chats from "./pages/Chats";
import MyProfile from "./pages/MyProfile";
import Professional from "./pages/Professional";
import Search from "./pages/Search";
import Home from "./pages/Home";
import LinkGoogle from "./pages/LinkGoogle";
import Login from "./pages/Login";
import Register from "./pages/Register";

function Header() {
  const { user, signOut } = useAuth();
  const [unread, setUnread] = useState(0);
  useEffect(() => {
    if (!user) { setUnread(0); return; }
    const load = () => api.unread().then(setUnread).catch(() => undefined);
    load();
    const t = setInterval(load, 15000);
    return () => clearInterval(t);
  }, [user]);
  return (
    <header className="topbar">
      <Link to="/" className="brand">Say<span>Serné</span></Link>
      <nav>
        <Link to="/search">Search</Link>
        {user?.role === "admin" && <Link to="/admin">Back office</Link>}
        {user ? (
          <>
            <Link to="/appointments">Appointments</Link>
            <Link to="/chats">Chats{unread > 0 && <span className="badge">{unread}</span>}</Link>
            <Link to="/alerts">Alerts</Link>
            <Link to="/me">Profile</Link>
            <span className="who">{user.firstName}</span>
            <button className="link" onClick={() => void signOut()}>Sign out</button>
          </>
        ) : (
          <>
            <Link to="/login">Sign in</Link>
            <Link to="/register" className="cta">Create account</Link>
          </>
        )}
      </nav>
    </header>
  );
}

/** UI guard only: the API enforces the same rule on every endpoint. */
function RequireUser({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  if (loading) return <p className="muted">Loading…</p>;
  if (!user) return <Navigate to="/login?error=login_required" replace />;
  return <>{children}</>;
}

/** UI guard only: the API enforces the same rule on every admin endpoint. */
function RequireAdmin({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  if (loading) return <p className="muted">Loading…</p>;
  if (!user) return <Navigate to="/login?error=login_required" replace />;
  if (user.role !== "admin") return <main className="card"><h1>Not authorised</h1><p>This area is for administrators.</p></main>;
  return <>{children}</>;
}

export default function App() {
  return (
    <>
      <Header />
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route path="/link-google" element={<LinkGoogle />} />
        <Route path="/search" element={<Search />} />
        <Route path="/professional/:id" element={<Professional />} />
        <Route path="/book/:id" element={<RequireUser><Book /></RequireUser>} />
        <Route path="/appointments" element={<RequireUser><Appointments /></RequireUser>} />
        <Route path="/chats" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/chats/:roomId" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/alerts" element={<RequireUser><Alerts /></RequireUser>} />
        <Route path="/me" element={<RequireUser><MyProfile /></RequireUser>} />
        <Route path="/admin" element={<RequireAdmin><Admin /></RequireAdmin>} />
        <Route path="*" element={<main className="card"><h1>Page not found</h1><Link to="/">Go home</Link></main>} />
      </Routes>
    </>
  );
}
