import { Link } from "react-router-dom";
import type { Listing } from "./api";

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
  return (
    <Link to={`/professional/${l.userId}`} className="listing">
      <div className="avatar">{l.firstName[0]}</div>
      <div className="grow">
        <strong>{fullName(l.firstName, l.lastName)}</strong>
        {l.isPremium && <span className="pill ok">premium</span>} {l.isStarred && <span className="pill">★ featured</span>}
        <div className="muted">{l.serviceName} · {l.categoryName}</div>
        <div className="muted">{[l.city, l.country].filter(Boolean).join(", ") || "—"} · {l.yearsOfExperience} yrs</div>
      </div>
      <div className="right">
        <Stars value={l.avgRating} count={l.totalReviews} />
        <div>{money(l.costPerHour)}</div>
      </div>
    </Link>
  );
}

export const DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
