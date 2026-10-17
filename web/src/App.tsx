import { Link, Navigate, Route, Routes } from "react-router-dom";
import { useEffect, useState, type ReactNode } from "react";
import { api, errorMessage } from "./api";
import { useConfig } from "./config";
import { PlacePicker } from "./place";
import { useAuth } from "./auth";
import Alerts from "./pages/Alerts";
import Appointments from "./pages/Appointments";
import Book from "./pages/Book";
import Chats from "./pages/Chats";
import ForgotPassword from "./pages/ForgotPassword";
import ResetPassword from "./pages/ResetPassword";
import Settings from "./pages/Settings";
import VerifyEmail from "./pages/VerifyEmail";
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
      <PlacePicker />
      <nav>
        <Link to="/search">Search</Link>
        {user ? (
          <>
            <Link to="/appointments">Appointments</Link>
            <Link to="/chats">Chats{unread > 0 && <span className="badge">{unread}</span>}</Link>
            <Link to="/alerts">Alerts</Link>
            <Link to="/me">Profile</Link>
            <Link to="/settings">Settings</Link>
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

/** Shown to a signed-in user whose address is not confirmed yet, but only while the site actually requires it (email on + the setting on). */
function VerifyBanner() {
  const { user } = useAuth();
  const cfg = useConfig();
  const [message, setMessage] = useState<string | null>(null);
  if (!user || user.emailVerified || !cfg.bool("Email.VerificationRequired")) return null;
  async function resend() {
    try { const r = await api.resendVerification(); setMessage(r.alreadyVerified ? "Your address is already confirmed." : "We sent you a new link. Please check your inbox."); }
    catch (e) { setMessage(errorMessage(e)); }
  }
  return <div className="notice" role="status">Please confirm your email address to book appointments or send messages. <button className="link" onClick={() => void resend()}>Send me a new link</button>{message && <span> — {message}</span>}</div>;
}

export default function App() {
  return (
    <>
      <Header />
      <VerifyBanner />
      <Routes>
        <Route path="/" element={<Home />} />
        <Route path="/login" element={<Login />} />
        <Route path="/register" element={<Register />} />
        <Route path="/link-google" element={<LinkGoogle />} />
        <Route path="/verify-email" element={<VerifyEmail />} />
        <Route path="/forgot-password" element={<ForgotPassword />} />
        <Route path="/reset-password" element={<ResetPassword />} />
        <Route path="/settings" element={<RequireUser><Settings /></RequireUser>} />
        <Route path="/search" element={<Search />} />
        <Route path="/professional/:id" element={<Professional />} />
        <Route path="/book/:id" element={<RequireUser><Book /></RequireUser>} />
        <Route path="/appointments" element={<RequireUser><Appointments /></RequireUser>} />
        <Route path="/chats" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/chats/:roomId" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/alerts" element={<RequireUser><Alerts /></RequireUser>} />
        <Route path="/me" element={<RequireUser><MyProfile /></RequireUser>} />
        <Route path="*" element={<main className="card"><h1>Page not found</h1><Link to="/">Go home</Link></main>} />
      </Routes>
    </>
  );
}
