import { Link } from "react-router-dom";
import { useState, type ChangeEvent } from "react";
import { api, errorMessage, type Listing, type UploadPurpose } from "./api";
import { useLookup } from "./config";

export function Stars({ value, count }: { value: number; count?: number }) {
  const full = Math.round(value);
  return (
    <span className="stars" aria-label={`${value.toFixed(1)} out of 5`}>
      {"★".repeat(full)}{"☆".repeat(5 - full)}
      {count !== undefined && <span className="muted"> ({count})</span>}
    </span>
  );
}

export const money = (n?: number) => (n === undefined || n === null ? "—" : `$${n.toFixed(2)}/h`);
export const fullName = (first: string, last: string) => `${first} ${last}`.trim();
export const when = (iso: string) => new Date(iso).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });

export function ListingCard({ l }: { l: Listing }) {
  const countries = useLookup("country");
  return (
    <Link to={`/professional/${l.userId}`} className="listing">
      <Avatar photo={l.profilePhoto} name={l.firstName} />
      <div className="grow">
        <strong>{fullName(l.firstName, l.lastName)}</strong>
        {l.isPremium && <span className="pill ok">premium</span>} {l.isStarred && <span className="pill">★ featured</span>}
        <div className="muted">{l.serviceName} · {l.categoryName}</div>
        <div className="muted">{[l.city, countries.label(l.country)].filter(Boolean).join(", ") || "—"} · {l.yearsOfExperience} yrs</div>
      </div>
      <div className="right">
        <Stars value={l.avgRating} count={l.totalReviews} />
        <div>{money(l.costPerHour)}</div>
      </div>
    </Link>
  );
}

/** A person's photo, or their initial when they have none. */
export function Avatar({ photo, name, big }: { photo?: string | null; name: string; big?: boolean }) {
  return photo
    ? <img className={`avatar photo ${big ? "big" : ""}`} src={photo} alt="" loading="lazy" />
    : <div className={`avatar ${big ? "big" : ""}`} aria-hidden="true">{name[0]}</div>;
}

/** Choose an image, upload it (the server shrinks and checks it) and hand back its address. */
export function PhotoPicker({ label, purpose, value, onChange }: { label: string; purpose: Extract<UploadPurpose, "profile_photo" | "banner_photo">; value?: string; onChange: (url?: string) => void }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function pick(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file) return;
    setBusy(true); setError(null);
    try { onChange((await api.uploadFile(file, purpose)).url); } catch (err) { setError(errorMessage(err)); } finally { setBusy(false); }
  }
  return (
    <div className="photo-picker">
      <span className="label">{label}</span>
      {value && <img src={value} alt="" className={purpose === "banner_photo" ? "banner-preview" : "avatar photo big"} />}
      <div className="row">
        <label className="btn small secondary file-btn">{busy ? "Uploading…" : value ? "Change" : "Upload"}
          <input type="file" accept="image/jpeg,image/png,image/gif,image/webp" onChange={pick} disabled={busy} hidden aria-label={label} /></label>
        {value && <button type="button" className="link" onClick={() => onChange(undefined)}>Remove</button>}
      </div>
      {error && <span className="field-error" role="alert">{error}</span>}
    </div>
  );
}

/** A <select> whose options come from an admin-maintained lookup. A value that has since been switched off stays selectable so editing never silently changes it. */
export function LookupSelect({ type, parent, value, onChange, ariaLabel, emptyLabel = "—" }: {
  type: string; parent?: string; value?: string; onChange: (code: string | undefined) => void; ariaLabel?: string; emptyLabel?: string | null;
}) {
  const { values, name } = useLookup(type, parent);
  const hasValue = !value || values.some(v => v.code === value);
  return (
    <select aria-label={ariaLabel} value={value ?? ""} onChange={e => onChange(e.target.value || undefined)}>
      {emptyLabel !== null && <option value="">{emptyLabel}</option>}
      {!hasValue && <option value={value}>{value}</option>}
      {values.map(v => <option key={v.code} value={v.code}>{name(v)}</option>)}
    </select>
  );
}

/** Free-text city with suggestions from the cities lookup for the chosen country. Cities are stored as typed (English names are suggested). */
export function CityInput({ country, value, onChange, label = "City" }: { country?: string; value: string; onChange: (v: string) => void; label?: string }) {
  const { values, name } = useLookup("city", country || "__none__");
  const id = `cities-${country ?? "any"}`;
  return (
    <label>{label}
      <input value={value} list={id} onChange={e => onChange(e.target.value)} autoComplete="off" />
      <datalist id={id}>{values.map(v => <option key={v.code} value={v.names.en ?? v.code} label={name(v)} />)}</datalist>
    </label>
  );
}

/** Day names from the "weekday" lookup (code 0 = Sunday). */
export function useWeekdays() {
  const { values, name } = useLookup("weekday");
  return { days: values, dayName: (n: number) => { const v = values.find(x => x.code === String(n)); return v ? name(v) : String(n); } };
}
