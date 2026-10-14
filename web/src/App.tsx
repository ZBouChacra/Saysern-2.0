import { Link, Navigate, Route, Routes } from "react-router-dom";
import type { ReactNode } from "react";
import { useAuth } from "./auth";
import Admin from "./pages/Admin";
import Home from "./pages/Home";
import LinkGoogle from "./pages/LinkGoogle";
import Login from "./pages/Login";
import Register from "./pages/Register";

function Header() {
  const { user, signOut } = useAuth();
  return (
    <header className="topbar">
      <Link to="/" className="brand">Say<span>Serné</span></Link>
      <nav>
        {user?.role === "admin" && <Link to="/admin">Back office</Link>}
        {user ? (
          <>
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
        <Route path="/admin" element={<RequireAdmin><Admin /></RequireAdmin>} />
        <Route path="*" element={<main className="card"><h1>Page not found</h1><Link to="/">Go home</Link></main>} />
      </Routes>
    </>
  );
}
