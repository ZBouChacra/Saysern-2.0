import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError, api, setTexts, type Session } from "./api";

interface SessionState {
  session: Session | null;
  loading: boolean;
  setSession: (s: Session | null) => void;
  can: (permission: string) => boolean;
  signOut: () => Promise<void>;
}
const Ctx = createContext<SessionState | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(null);
  const [loading, setLoading] = useState(true);
  useEffect(() => {
    api.translations().then(setTexts).catch(() => undefined);
    api.me().then(setSession).catch(e => { if (!(e instanceof ApiError && e.status === 401)) console.error(e); setSession(null); }).finally(() => setLoading(false));
  }, []);
  const can = useCallback((p: string) => !!session && (session.isSuper || session.permissions.includes(p)), [session]);
  const signOut = useCallback(async () => { await api.logout().catch(() => undefined); setSession(null); }, []);
  const value = useMemo(() => ({ session, loading, setSession, can, signOut }), [session, loading, can, signOut]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useSession(): SessionState {
  const v = useContext(Ctx);
  if (!v) throw new Error("useSession must be used inside <SessionProvider>");
  return v;
}
