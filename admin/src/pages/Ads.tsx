import { useCallback, useEffect, useMemo, useState, type ChangeEvent } from "react";
import { api, errorMessage, type Ad, type AdBatch, type AdStat, type CapacityDay, type LookupValue } from "../api";
import { Link } from "react-router-dom";
import { useSession } from "../session";
import PaymentForm from "./PaymentForm";

const label = (v: LookupValue) => v.names.en ?? v.code;
const today = () => new Date().toISOString().slice(0, 10);
const daysBetween = (from: string, to: string) => Math.round((Date.parse(to) - Date.parse(from)) / 86400000) + 1;
const EMPTY = { title: "", position: "", imageFileId: "", linkUrl: "https://", countryCode: "", city: "", isActive: true, feeOverride: "" };

export default function Ads() {
  const { can } = useSession();
  const manage = can("ads.manage");
  const [ads, setAds] = useState<Ad[]>([]);
  const [stats, setStats] = useState<Record<number, AdStat>>({});
  const [positions, setPositions] = useState<LookupValue[]>([]);
  const [countries, setCountries] = useState<LookupValue[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<number | "new" | null>(null);
  const [form, setForm] = useState(EMPTY);
  const [uploading, setUploading] = useState(false);
  const [batchesFor, setBatchesFor] = useState<Ad | null>(null);

  const load = useCallback(async () => {
    try {
      const [a, s, p, c] = await Promise.all([api.ads(), api.adStats(), api.lookupValues("ad_position"), api.lookupValues("country")]);
      setAds(a); setStats(Object.fromEntries(s.map(x => [x.adId, x]))); setPositions(p.filter(v => v.isActive)); setCountries(c.filter(v => v.isActive)); setError(null);
    } catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const posName = (code: string) => { const v = positions.find(p => p.code === code); return v ? label(v) : code; };
  const placeOf = (a: Ad) => a.countryCode ? [a.city, label(countries.find(c => c.code === a.countryCode) ?? { names: {}, code: a.countryCode } as LookupValue)].filter(Boolean).join(", ") : "Worldwide";

  function startNew() { setForm({ ...EMPTY, position: positions[0]?.code ?? "" }); setEditing("new"); setError(null); }
  function startEdit(a: Ad) { setForm({ title: a.title, position: a.position, imageFileId: a.imageFileId, linkUrl: a.linkUrl, countryCode: a.countryCode ?? "", city: a.city ?? "", isActive: a.isActive, feeOverride: a.feeOverride === undefined ? "" : String(a.feeOverride) }); setEditing(a.id); setError(null); }

  async function pick(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]; e.target.value = "";
    if (!file) return;
    setUploading(true); setError(null);
    try { const up = await api.uploadAdImage(file); setForm(f => ({ ...f, imageFileId: up.id })); } catch (err) { setError(errorMessage(err)); } finally { setUploading(false); }
  }

  async function save() {
    setError(null);
    try {
      await api.saveAd(editing === "new" ? null : editing as number, { title: form.title, position: form.position, imageFileId: form.imageFileId, linkUrl: form.linkUrl, countryCode: form.countryCode || undefined,
        city: form.countryCode ? form.city.trim() || undefined : undefined, isActive: form.isActive, feeOverride: form.feeOverride === "" ? undefined : Number(form.feeOverride) });
      setEditing(null); await load();
    } catch (err) { setError(errorMessage(err)); }
  }

  const ctr = (s?: AdStat) => (s && s.impressions > 0 ? `${((s.clicks / s.impressions) * 100).toFixed(1)}%` : "—");

  return (
    <main className="card full">
      <h1>Ads</h1>
      <p className="muted">An ad runs only on days covered by a <b>paid</b> batch, and only for visitors in its place. Reserve days under <b>Batches</b>; the capacity preview shows whether the days are free.</p>
      {error && <p className="error" role="alert">{error}</p>}
      {editing === null && <p className="row">{manage && <button className="btn" onClick={startNew} disabled={positions.length === 0}>New ad</button>}<button className="btn secondary" onClick={() => void load()}>Refresh numbers</button></p>}

      {editing !== null && manage && (
        <section className="card" aria-label="Ad form">
          <h2>{editing === "new" ? "New ad" : "Edit ad"}</h2>
          <label>Title<input value={form.title} maxLength={150} onChange={e => setForm({ ...form, title: e.target.value })} /></label>
          <label>Position<select value={form.position} onChange={e => setForm({ ...form, position: e.target.value })}>{positions.map(p => <option key={p.code} value={p.code}>{label(p)}</option>)}</select></label>
          <p className="muted small">Suggested size for this position: {(positions.find(p => p.code === form.position)?.metadata as { size?: string } | undefined)?.size ?? "—"} pixels.</p>
          <div className="photo-picker">
            <span className="label">Picture</span>
            {form.imageFileId && <img src={`/api/files/${form.imageFileId}`} alt="Ad preview" style={{ maxWidth: 320, maxHeight: 140, display: "block", marginBottom: ".4rem" }} />}
            <label className="btn small secondary file-btn">{uploading ? "Uploading…" : form.imageFileId ? "Change picture" : "Upload picture"}<input type="file" hidden aria-label="Ad picture" accept="image/jpeg,image/png,image/gif,image/webp" onChange={pick} disabled={uploading} /></label>
          </div>
          <label>Link (where a click goes)<input type="url" value={form.linkUrl} onChange={e => setForm({ ...form, linkUrl: e.target.value })} /></label>
          <div className="row">
            <label>Country<select value={form.countryCode} onChange={e => setForm({ ...form, countryCode: e.target.value, city: "" })}><option value="">Worldwide</option>{countries.map(c => <option key={c.code} value={c.code}>{label(c)}</option>)}</select></label>
            <label>City (optional)<input value={form.city} disabled={!form.countryCode} onChange={e => setForm({ ...form, city: e.target.value })} /></label>
            <label>Special fee per day (optional)<input type="number" min="0" step="0.01" value={form.feeOverride} onChange={e => setForm({ ...form, feeOverride: e.target.value })} /></label>
          </div>
          <label className="row"><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} /> Active (switching off frees its reserved days)</label>
          <div className="row"><button className="btn" onClick={() => void save()} disabled={!form.title.trim() || !form.imageFileId || uploading}>Save</button><button className="btn secondary" onClick={() => setEditing(null)}>Cancel</button></div>
        </section>
      )}

      <div className="table-wrap"><table className="t"><thead><tr><th>Ad</th><th>Position</th><th>Place</th><th>Status</th><th>Seen</th><th>Clicks</th><th>CTR</th><th /></tr></thead><tbody>
        {ads.map(a => { const s = stats[a.id]; return (
          <tr key={a.id}><td><img src={`/api/files/${a.imageFileId}`} alt="" style={{ height: 28, verticalAlign: "middle", marginInlineEnd: 8, borderRadius: 3 }} />{a.title}</td><td>{posName(a.position)}</td><td>{placeOf(a)}</td>
            <td>{!a.isActive ? <span className="pill">off</span> : a.runningToday ? <span className="pill ok">running</span> : <span className="pill">not running</span>}</td>
            <td>{s ? `${s.impressions} (${s.uniqueImpressions} unique)` : 0}</td><td>{s ? `${s.clicks} (${s.uniqueClicks} unique)` : 0}</td><td>{ctr(s)}</td>
            <td className="row"><button className="btn small secondary" onClick={() => setBatchesFor(a)}>Batches</button>{manage && <button className="btn small secondary" onClick={() => startEdit(a)}>Edit</button>}</td></tr>); })}
        {ads.length === 0 && <tr><td colSpan={8} className="muted">No ads yet.</td></tr>}
      </tbody></table></div>

      {batchesFor && <Batches ad={batchesFor} manage={manage} onChanged={() => void load()} onClose={() => { setBatchesFor(null); void load(); }} />}
    </main>
  );
}

function Batches({ ad, manage, onClose, onChanged }: { ad: Ad; manage: boolean; onClose: () => void; onChanged: () => void }) {
  const [batches, setBatches] = useState<AdBatch[]>([]);
  const [from, setFrom] = useState(today());
  const [to, setTo] = useState(today());
  const [preview, setPreview] = useState<{ days: CapacityDay[]; fee: number } | null>(null);
  const [paying, setPaying] = useState<AdBatch | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => { try { setBatches(await api.adBatches(ad.id)); } catch (e) { setError(errorMessage(e)); } }, [ad.id]);
  useEffect(() => { void load(); }, [load]);

  async function check() {
    setError(null); setPreview(null);
    try {
      const [days, price] = await Promise.all([api.adCapacity(ad.id, from, to), api.resolveAdPricing(ad.position, ad.countryCode, ad.city)]);
      setPreview({ days, fee: ad.feeOverride ?? price.dailyFee });
    } catch (e) { setError(errorMessage(e)); }
  }
  async function reserve() { setError(null); try { await api.createAdBatch(ad.id, from, to); setPreview(null); await load(); onChanged(); } catch (e) { setError(errorMessage(e)); await check(); } }
  async function move(b: AdBatch, status: "paid" | "cancelled") { setError(null); try { await api.setAdBatchStatus(b.id, status); await load(); onChanged(); } catch (e) { setError(errorMessage(e)); } }

  const full = useMemo(() => preview?.days.filter(d => d.booked + 1 > d.maxAds) ?? [], [preview]);
  const valid = to >= from && from >= today();
  return (
    <section className="card" aria-label={`Batches of ${ad.title}`}>
      <div className="row spread"><h2>Batches: {ad.title}</h2><button className="btn small secondary" onClick={onClose}>Close</button></div>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="table-wrap"><table className="t"><thead><tr><th>From</th><th>To</th><th>Days</th><th>Fee per day</th><th>Total (USD)</th><th>Status</th><th>Receipt</th><th /></tr></thead><tbody>
        {batches.map(b => (<tr key={b.id}><td>{b.dateFrom}</td><td>{b.dateTo}</td><td>{daysBetween(b.dateFrom, b.dateTo)}</td><td>{b.feePerDay.toFixed(2)}</td><td>{b.totalFee.toFixed(2)}</td>
          <td><span className={`pill ${b.status === "paid" ? "ok" : b.status === "cancelled" ? "bad" : ""}`}>{b.status}</span></td>
          <td>{b.receiptId ? <Link to={`/payments/${b.receiptId}`}>{b.receiptNumber}</Link> : <span className="muted">—</span>}</td>
          <td className="row">{manage && b.status === "pending" && <button className="btn small" onClick={() => setPaying(b)}>Record payment</button>}{manage && b.status !== "cancelled" && <button className="btn small secondary" onClick={() => void move(b, "cancelled")}>Cancel</button>}</td></tr>))}
        {batches.length === 0 && <tr><td colSpan={8} className="muted">No batches yet: this ad will not run.</td></tr>}
      </tbody></table></div>
      {paying && <PaymentForm title={`${ad.title}, ${paying.dateFrom} to ${paying.dateTo}`} defaultBilledTo={ad.title} amount={paying.totalFee} onCancel={() => setPaying(null)}
        onSubmit={async billing => { await api.setAdBatchStatus(paying.id, "paid", billing); setPaying(null); await load(); onChanged(); }} />}
      {manage && ad.isActive && (
        <>
          <h3>Reserve days</h3>
          <div className="toolbar">
            <label>From<input type="date" aria-label="From" min={today()} value={from} onChange={e => { setFrom(e.target.value); if (e.target.value > to) setTo(e.target.value); setPreview(null); }} /></label>
            <label>To<input type="date" aria-label="To" min={from} value={to} onChange={e => { setTo(e.target.value); setPreview(null); }} /></label>
            <button className="btn secondary" disabled={!valid} onClick={() => void check()}>Check availability</button>
            <button className="btn" disabled={!valid || (preview !== null && full.length > 0)} onClick={() => void reserve()}>Reserve</button>
          </div>
          {preview && (
            <div data-testid="capacity-preview">
              <p>{daysBetween(from, to)} day(s) at {preview.fee.toFixed(2)} USD = <b>{(preview.fee * daysBetween(from, to)).toFixed(2)} USD</b>. {full.length === 0 ? "All days are available." : <span className="error">{full.length} day(s) are full: {full.map(d => d.day).join(", ")}.</span>}</p>
              <div className="table-wrap"><table className="t"><thead><tr><th>Day</th><th>Other ads booked</th><th>Limit</th><th /></tr></thead><tbody>
                {preview.days.map(d => (<tr key={d.day}><td>{d.day}</td><td>{d.booked}</td><td>{d.maxAds}</td><td>{d.booked + 1 > d.maxAds ? <span className="pill bad">full</span> : <span className="pill ok">free</span>}</td></tr>))}
              </tbody></table></div>
            </div>
          )}
        </>
      )}
    </section>
  );
}
