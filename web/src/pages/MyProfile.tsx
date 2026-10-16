import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { ApiError, api, errorMessage, type Category, type PrivateProfile, type Profession, type ProfessionInput, type Service, type Slot } from "../api";
import { CityInput, LookupSelect, useWeekdays } from "../components";
import { useConfig } from "../config";

const blankToUndef = (v: string | undefined) => (v === undefined || v.trim() === "" ? undefined : v.trim());

export default function MyProfile() {
  const [profile, setProfile] = useState<PrivateProfile | null>(null);
  const [professions, setProfessions] = useState<Profession[]>([]);
  const [availability, setAvailability] = useState<Slot[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [p, pro, av] = await Promise.all([api.myProfile(), api.myProfessions(), api.myAvailability()]);
      setProfile(p); setProfessions(pro); setAvailability(av);
    } catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  if (!profile) return <main className="card"><p className="muted">{error ?? "Loading…"}</p></main>;
  return (
    <main className="card wide">
      <h1>My profile</h1>
      <ProfileForm profile={profile} onSaved={p => { setProfile(p); void load(); }} />
      {profile.profileType === "professional" ? (
        <>
          <h2>My services</h2>
          {professions.length === 0 && <p className="muted">Add the services you offer so clients can find you.</p>}
          {professions.map(p => <ProfessionEditor key={p.id} p={p} slots={availability.filter(a => a.professionId === p.id)} onChanged={load} />)}
          <AddProfession onAdded={load} />
          <p><Link to={`/professional/${profile.id}`}>View my public page</Link></p>
        </>
      ) : <p className="muted">Switch your profile type to <b>professional</b> to list services and receive bookings.</p>}
    </main>
  );
}

function ProfileForm({ profile, onSaved }: { profile: PrivateProfile; onSaved: (p: PrivateProfile) => void }) {
  const maxBio = useConfig().num("Profile.MaxBioLength");
  const [f, setF] = useState({ ...profile });
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const set = (patch: Partial<PrivateProfile>) => setF(v => ({ ...v, ...patch }));

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true); setMessage(null); setFieldErrors({});
    try {
      const saved = await api.saveProfile({
        firstName: f.firstName, lastName: f.lastName, phone: blankToUndef(f.phone), sex: blankToUndef(f.sex), dateOfBirth: blankToUndef(f.dateOfBirth),
        nationality: blankToUndef(f.nationality), country: blankToUndef(f.country), city: blankToUndef(f.city), bio: blankToUndef(f.bio),
        portfolio: blankToUndef(f.portfolio), profilePhoto: blankToUndef(f.profilePhoto), bannerPhoto: blankToUndef(f.bannerPhoto),
        preferredLanguage: f.preferredLanguage, profileType: f.profileType,
      });
      onSaved(saved); setMessage({ ok: true, text: "Saved." });
    } catch (err) {
      if (err instanceof ApiError && err.fieldErrors) setFieldErrors(err.fieldErrors);
      setMessage({ ok: false, text: errorMessage(err) });
    } finally { setBusy(false); }
  }

  const text = (k: keyof PrivateProfile, label: string, type = "text") => (
    <label>{label}
      <input type={type} value={(f[k] as string | undefined) ?? ""} onChange={e => set({ [k]: e.target.value } as Partial<PrivateProfile>)} aria-invalid={!!fieldErrors[k]} />
      {fieldErrors[k] && <span className="field-error">{fieldErrors[k]}</span>}
    </label>
  );
  return (
    <form onSubmit={submit} noValidate className="panel">
      <div className="grid2">{text("firstName", "First name")}{text("lastName", "Last name")}</div>
      <div className="grid2">{text("phone", "Phone")}{text("dateOfBirth", "Date of birth", "date")}</div>
      <div className="grid2">
        <label>Sex<LookupSelect type="sex" value={f.sex} onChange={v => set({ sex: v })} /></label>
        <label>Nationality<LookupSelect type="nationality" value={f.nationality} onChange={v => set({ nationality: v })} /></label>
      </div>
      <div className="grid2">
        <label>Country<LookupSelect type="country" value={f.country} onChange={v => set({ country: v })} /></label>
        <CityInput country={f.country} value={f.city ?? ""} onChange={v => set({ city: v })} />
      </div>
      <label>About you<textarea rows={3} value={f.bio ?? ""} onChange={e => set({ bio: e.target.value })} maxLength={maxBio} /></label>
      <div className="grid2">
        <label>I am a
          <select value={f.profileType} onChange={e => set({ profileType: e.target.value as PrivateProfile["profileType"] })}>
            <option value="customer">customer</option><option value="professional">professional</option></select>
          {fieldErrors.profileType && <span className="field-error">{fieldErrors.profileType}</span>}</label>
        <label>Language<select value={f.preferredLanguage} onChange={e => set({ preferredLanguage: e.target.value as "en" | "ar" })}><option value="en">English</option><option value="ar">العربية</option></select></label>
      </div>
      {message && <p className={message.ok ? "ok-text" : "error"} role="alert">{message.text}</p>}
      <button className="btn" disabled={busy}>{busy ? "Saving…" : "Save profile"}</button>
      <p className="hint">Your email and phone are never shown on your public page.</p>
    </form>
  );
}

function ProfessionEditor({ p, slots, onChanged }: { p: Profession; slots: Slot[]; onChanged: () => Promise<void> }) {
  const [f, setF] = useState({
    city: p.city ?? "", costPerHour: p.costPerHour?.toString() ?? "", yearsOfExperience: p.yearsOfExperience.toString(), website: p.website ?? "",
    hasTeam: p.hasTeam, teamSize: p.teamSize.toString(), hasOffice: p.hasOffice, officeAddress: p.officeAddress ?? "", areas: p.geographicAreas.join(", "), isHidden: p.isHidden,
  });
  const weekdays = useWeekdays();
  const [rows, setRows] = useState<Slot[]>(slots);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  async function save() {
    setError(null); setNote(null);
    const body: ProfessionInput = {
      city: blankToUndef(f.city), costPerHour: f.costPerHour === "" ? null : Number(f.costPerHour), yearsOfExperience: Number(f.yearsOfExperience) || 0,
      website: blankToUndef(f.website), hasTeam: f.hasTeam, teamSize: Number(f.teamSize) || 0, hasOffice: f.hasOffice, officeAddress: blankToUndef(f.officeAddress),
      geographicAreas: f.areas.split(",").map(a => a.trim()).filter(Boolean), isHidden: f.isHidden,
    };
    try { await api.updateProfession(p.id, body); await api.saveAvailability(p.id, rows.map(r => ({ dayOfWeek: r.dayOfWeek, startTime: r.startTime, endTime: r.endTime }))); setNote("Saved."); await onChanged(); }
    catch (e) { setError(errorMessage(e)); }
  }
  async function remove() {
    if (!window.confirm(`Remove ${p.serviceName}?`)) return;
    try { await api.deleteProfession(p.id); await onChanged(); } catch (e) { setError(errorMessage(e)); }
  }
  const setRow = (i: number, patch: Partial<Slot>) => setRows(r => r.map((x, k) => (k === i ? { ...x, ...patch } : x)));

  return (
    <div className="panel">
      <div className="row spread"><strong>{p.serviceName}</strong><span className="muted">{p.categoryName}{p.isLocked ? " · locked by an administrator" : ""}</span></div>
      <div className="grid2">
        <label>City<input value={f.city} onChange={e => setF({ ...f, city: e.target.value })} /></label>
        <label>Cost per hour ($)<input type="number" min={0} value={f.costPerHour} onChange={e => setF({ ...f, costPerHour: e.target.value })} /></label>
        <label>Years of experience<input type="number" min={0} max={80} value={f.yearsOfExperience} onChange={e => setF({ ...f, yearsOfExperience: e.target.value })} /></label>
        <label>Website<input value={f.website} onChange={e => setF({ ...f, website: e.target.value })} /></label>
      </div>
      <label>Areas you serve (comma separated)<input value={f.areas} onChange={e => setF({ ...f, areas: e.target.value })} /></label>
      <div className="row">
        <label className="check"><input type="checkbox" checked={f.hasTeam} onChange={e => setF({ ...f, hasTeam: e.target.checked })} /> I have a team</label>
        {f.hasTeam && <input type="number" min={0} value={f.teamSize} onChange={e => setF({ ...f, teamSize: e.target.value })} aria-label="Team size" style={{ width: 90 }} />}
        <label className="check"><input type="checkbox" checked={f.hasOffice} onChange={e => setF({ ...f, hasOffice: e.target.checked })} /> I have an office</label>
        <label className="check"><input type="checkbox" checked={f.isHidden} onChange={e => setF({ ...f, isHidden: e.target.checked })} /> Hide this listing</label>
      </div>
      {f.hasOffice && <label>Office address<input value={f.officeAddress} onChange={e => setF({ ...f, officeAddress: e.target.value })} /></label>}

      <h4>Weekly availability</h4>
      {rows.map((r, i) => (
        <div className="row" key={i}>
          <select aria-label="Day" value={r.dayOfWeek} onChange={e => setRow(i, { dayOfWeek: Number(e.target.value) })}>{weekdays.days.map(d => <option key={d.code} value={Number(d.code)}>{weekdays.dayName(Number(d.code))}</option>)}</select>
          <input type="time" aria-label="From" value={r.startTime} onChange={e => setRow(i, { startTime: e.target.value })} />
          <input type="time" aria-label="To" value={r.endTime} onChange={e => setRow(i, { endTime: e.target.value })} />
          <button type="button" className="btn small secondary" onClick={() => setRows(rows.filter((_, k) => k !== i))}>Remove</button>
        </div>
      ))}
      <button type="button" className="btn small secondary" onClick={() => setRows([...rows, { dayOfWeek: 1, startTime: "09:00", endTime: "17:00" }])}>Add a time window</button>
      {error && <p className="error" role="alert">{error}</p>}
      {note && <p className="ok-text">{note}</p>}
      <div className="row"><button className="btn" onClick={() => void save()}>Save this service</button><button className="btn secondary" onClick={() => void remove()}>Remove</button></div>
    </div>
  );
}

function AddProfession({ onAdded }: { onAdded: () => Promise<void> }) {
  const [categories, setCategories] = useState<Category[]>([]);
  const [services, setServices] = useState<Service[]>([]);
  const [categoryId, setCategoryId] = useState<number | undefined>();
  const [serviceId, setServiceId] = useState<number | undefined>();
  const [cost, setCost] = useState("");
  const [years, setYears] = useState("0");
  const [city, setCity] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { api.categories().then(setCategories).catch(() => undefined); }, []);
  useEffect(() => { setServiceId(undefined); if (categoryId) api.services(categoryId).then(setServices).catch(() => setServices([])); else setServices([]); }, [categoryId]);

  async function add(e: FormEvent) {
    e.preventDefault();
    setError(null);
    try {
      await api.addProfession({ categoryId, serviceId, costPerHour: cost === "" ? null : Number(cost), yearsOfExperience: Number(years) || 0, city: blankToUndef(city) });
      setCost(""); setYears("0"); setCity(""); setCategoryId(undefined); await onAdded();
    } catch (err) { setError(errorMessage(err)); }
  }
  return (
    <form className="panel" onSubmit={add}>
      <h3>Add a service</h3>
      <div className="grid2">
        <label>Category<select value={categoryId ?? ""} onChange={e => setCategoryId(e.target.value ? Number(e.target.value) : undefined)}><option value="">Choose…</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
        <label>Service<select value={serviceId ?? ""} onChange={e => setServiceId(e.target.value ? Number(e.target.value) : undefined)} disabled={!categoryId}><option value="">Choose…</option>{services.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
        <label>Cost per hour ($)<input type="number" min={0} value={cost} onChange={e => setCost(e.target.value)} /></label>
        <label>Years of experience<input type="number" min={0} max={80} value={years} onChange={e => setYears(e.target.value)} /></label>
      </div>
      <label>City<input value={city} onChange={e => setCity(e.target.value)} /></label>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="btn" disabled={!categoryId || !serviceId}>Add service</button>
    </form>
  );
}
