import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { api } from "./api";
import { useAuth } from "./auth";
import { CityInput, LookupSelect } from "./components";
import { useLookup } from "./config";

export interface Place { country?: string; city?: string }
interface PlaceState extends Place { source: "saved" | "profile" | "none"; setPlace: (p: Place) => void; clear: () => void }

const KEY = "sayserne.place";
const Ctx = createContext<PlaceState>({ source: "none", setPlace: () => undefined, clear: () => undefined });

function readSaved(): Place | null {
  try {
    const raw = localStorage.getItem(KEY);
    const p = raw ? JSON.parse(raw) as Place : null;
    return p && (p.country || p.city) ? { country: p.country || undefined, city: p.city || undefined } : null;
  } catch { return null; }
}

/** Where the visitor is: a place they chose (kept in this browser), otherwise the one on their profile. Ads and search ranking use it. */
export function PlaceProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth();
  const [saved, setSaved] = useState<Place | null>(() => readSaved());
  const [profile, setProfile] = useState<Place | null>(null);

  useEffect(() => {
    if (!user) { setProfile(null); return; }
    let live = true;
    api.myProfile().then(p => { if (live) setProfile(p.country || p.city ? { country: p.country || undefined, city: p.city || undefined } : null); }).catch(() => { if (live) setProfile(null); });
    return () => { live = false; };
  }, [user]);

  const setPlace = useCallback((p: Place) => {
    const next = p.country || p.city ? { country: p.country || undefined, city: p.city || undefined } : null;
    setSaved(next);
    try { if (next) localStorage.setItem(KEY, JSON.stringify(next)); else localStorage.removeItem(KEY); } catch { /* storage unavailable: the choice lasts for this visit */ }
  }, []);

  const value = useMemo<PlaceState>(() => {
    const active = saved ?? profile;
    return { country: active?.country, city: active?.city, source: saved ? "saved" : profile ? "profile" : "none", setPlace, clear: () => setPlace({}) };
  }, [saved, profile, setPlace]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export const usePlace = () => useContext(Ctx);

/** "Lebanon, Beirut" style label for the header. */
function usePlaceLabel(p: Place): string {
  const countries = useLookup("country");
  const c = p.country ? countries.values.find(v => v.code === p.country) : undefined;
  return [p.city, c ? countries.name(c) : p.country].filter(Boolean).join(", ");
}

export function PlacePicker() {
  const place = usePlace();
  const [country, setCountry] = useState<string | undefined>(place.country);
  const [city, setCity] = useState(place.city ?? "");
  const label = usePlaceLabel(place);
  useEffect(() => { setCountry(place.country); setCity(place.city ?? ""); }, [place.country, place.city]);
  return (
    <details className="place-picker">
      <summary aria-label="Your location">📍 {label || "Set your location"}</summary>
      <div className="place-pop">
        <p className="muted small">We use this to show you ads and professionals near you.</p>
        <label>Country<LookupSelect type="country" value={country} onChange={v => { setCountry(v); setCity(""); }} emptyLabel="Not set" /></label>
        <CityInput country={country} value={city} onChange={setCity} />
        <div className="row">
          <button type="button" className="btn small" onClick={e => { place.setPlace({ country, city: city.trim() || undefined }); (e.currentTarget.closest("details") as HTMLDetailsElement).open = false; }}>Save</button>
          {place.source === "saved" && <button type="button" className="link" onClick={() => place.clear()}>Forget it</button>}
        </div>
      </div>
    </details>
  );
}
