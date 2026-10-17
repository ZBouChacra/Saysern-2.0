import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError, api, type User } from "./api";
import { useLang } from "./config";
import { setTimeZone } from "./time";

let languageAppliedFor: number | null = null;   // the saved language is applied when a person signs in, once (a later choice in the header must not be undone)

interface AuthState {
  user: User | null;
  loading: boolean;
  setUser: (user: User | null) => void;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUserState] = useState<User | null>(null);
  const [loading, setLoading] = useState(true);
  const { lang, setLang, languages } = useLang();
  const setUser = useCallback((u: User | null) => {
    setTimeZone(u?.timeZone);
    setUserState(u);
    if (!u) { languageAppliedFor = null; return; }
    if (languageAppliedFor !== u.id) {
      languageAppliedFor = u.id;
      if (u.preferredLanguage && u.preferredLanguage !== lang && languages.some(l => l.code === u.preferredLanguage)) setLang(u.preferredLanguage);
    }
  }, [lang, languages, setLang]);

  useEffect(() => {
    api.me()
      .then(setUser)
      .catch(e => { if (!(e instanceof ApiError && e.status === 401)) console.error(e); setUser(null); })
      .finally(() => setLoading(false));
  }, []);

  const signOut = useCallback(async () => {
    await api.logout().catch(() => undefined);
    setUser(null);
  }, [setUser]);

  const value = useMemo(() => ({ user, loading, setUser, signOut }), [user, loading, signOut]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
  return ctx;
}
