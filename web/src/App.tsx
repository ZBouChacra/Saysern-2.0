import { Link, Navigate, Route, Routes } from "react-router-dom";
import { useEffect, useState, type ReactNode } from "react";
import { api, errorMessage } from "./api";
import { useConfig, useLang } from "./config";
import { t } from "./texts";
import { PlacePicker } from "./place";
import { useAuth } from "./auth";
import Alerts from "./pages/Alerts";
import Appointments from "./pages/Appointments";
import Book from "./pages/Book";
import Chats from "./pages/Chats";
import Contact from "./pages/Contact";
import Reenable from "./pages/Reenable";
import ContentPage from "./pages/ContentPage";
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

/** Switches the whole site's language (texts, direction). For a signed-in person the choice is also saved to their account. */
function LanguageSwitcher() {
  const { lang, setLang, languages } = useLang();
  const { user, setUser } = useAuth();
  if (languages.length < 2) return null;
  async function choose(code: string) {
    if (user) { try { setUser(await api.updatePreferences(code, user.timeZone)); } catch { /* the choice still applies on this device */ } }
    setLang(code);
  }
  return (
    <label className="lang-switch"><span className="sr-only">{t("Language")}</span>
      <select aria-label={t("Language")} value={lang} onChange={e => void choose(e.target.value)}>{languages.map(l => <option key={l.code} value={l.code}>{l.names[l.code] ?? l.names.en ?? l.code}</option>)}</select>
    </label>
  );
}

/** The site's name from Settings (the last capital letter starts the highlighted part: "Say" + "Serné"), or its logo when an admin uploaded one. */
function Brand() {
  const cfg = useConfig();
  const name = cfg.str("Site.Name") || "SaySerné";
  const logo = cfg.str("Brand.LogoUrl");
  if (logo) return <img className="brand-logo" src={logo} alt={name} />;
  const cut = name.search(/[A-Z](?![A-Z]*$)/) >= 0 ? name.search(/[A-Z](?=[^A-Z]*$)/) : -1;
  return cut > 0 ? <>{name.slice(0, cut)}<span>{name.slice(cut)}</span></> : <>{name}</>;
}

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
      <Link to="/" className="brand"><Brand /></Link>
      <PlacePicker />
      <LanguageSwitcher />
      <nav>
        <Link to="/search">{t("Search")}</Link>
        {user ? (
          <>
            <Link to="/appointments">{t("Appointments")}</Link>
            <Link to="/chats">{t("Chats")}{unread > 0 && <span className="badge">{unread}</span>}</Link>
            <Link to="/alerts">{t("Alerts")}</Link>
            <Link to="/me">{t("Profile")}</Link>
            <Link to="/settings">{t("Settings")}</Link>
            <span className="who">{user.firstName}</span>
            <button className="link" onClick={() => void signOut()}>{t("Sign out")}</button>
          </>
        ) : (
          <>
            <Link to="/login">{t("Sign in")}</Link>
            <Link to="/register" className="cta">{t("Create account")}</Link>
          </>
        )}
      </nav>
    </header>
  );
}

/** UI guard only: the API enforces the same rule on every endpoint. */
function RequireUser({ children }: { children: ReactNode }) {
  const { user, loading } = useAuth();
  if (loading) return <p className="muted">{t("Loading…")}</p>;
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
    try { const r = await api.resendVerification(); setMessage(r.alreadyVerified ? t("Your address is already confirmed.") : t("We sent you a new link. Please check your inbox.")); }
    catch (e) { setMessage(errorMessage(e)); }
  }
  return <div className="notice" role="status">{t("Please confirm your email address to book appointments or send messages.")}{" "}<button className="link" onClick={() => void resend()}>{t("Send me a new link")}</button>{message && <span> — {message}</span>}</div>;
}

function Footer() {
  return (
    <footer className="site-footer">
      <nav aria-label={t("Footer")}><Link to="/about">{t("About")}</Link><Link to="/terms">{t("Terms")}</Link><Link to="/privacy">{t("Privacy")}</Link><Link to="/contact">{t("Contact")}</Link></nav>
    </footer>
  );
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
        <Route path="/about" element={<ContentPage page="about" />} />
        <Route path="/terms" element={<ContentPage page="terms" />} />
        <Route path="/privacy" element={<ContentPage page="privacy" />} />
        <Route path="/contact" element={<Contact />} />
        <Route path="/verify-email" element={<VerifyEmail />} />
        <Route path="/forgot-password" element={<ForgotPassword />} />
        <Route path="/reset-password" element={<ResetPassword />} />
        <Route path="/reenable" element={<Reenable />} />
        <Route path="/settings" element={<RequireUser><Settings /></RequireUser>} />
        <Route path="/search" element={<Search />} />
        <Route path="/professional/:id" element={<Professional />} />
        <Route path="/book/:id" element={<RequireUser><Book /></RequireUser>} />
        <Route path="/appointments" element={<RequireUser><Appointments /></RequireUser>} />
        <Route path="/chats" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/chats/:roomId" element={<RequireUser><Chats /></RequireUser>} />
        <Route path="/alerts" element={<RequireUser><Alerts /></RequireUser>} />
        <Route path="/me" element={<RequireUser><MyProfile /></RequireUser>} />
        <Route path="*" element={<main className="card"><h1>{t("Page not found")}</h1><Link to="/">{t("Go home")}</Link></main>} />
      </Routes>
      <Footer />
    </>
  );
}
