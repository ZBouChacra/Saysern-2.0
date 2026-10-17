import { useCallback, useEffect, useMemo, useState } from "react";
import { api, errorMessage, type LookupValue, type PremiumBatch, type PremiumCandidate, type PremiumCapacityDay, type PremiumPricing, type ResolvedPremiumPricing } from "../api";
import { Link } from "react-router-dom";
import { useSession } from "../session";
import PaymentForm from "./PaymentForm";

const label = (v: LookupValue) => v.names.en ?? v.code;
const today = () => new Date().toISOString().slice(0, 10);
const daysBetween = (a: string, b: string) => Math.round((Date.parse(b) - Date.parse(a)) / 86400000) + 1;

/**
 * Premium listings. Staff book calendar days for a listing; when a batch is marked PAID it takes premium slots (first paid, first served). Each country has a limit
 * of premium listings per day: a batch that finds a day full is pushed later so it still gets exactly the days that were paid for.
 */
export default function Premium() {
  const { can } = useSession();
  const manage = can("premium.manage");
  const [batches, setBatches] = useState<PremiumBatch[]>([]);
  const [countries, setCountries] = useState<LookupValue[]>([]);
  const [prices, setPrices] = useState<PremiumPricing[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [paying, setPaying] = useState<PremiumBatch | null>(null);

  const load = useCallback(async () => {
    try { const [b, c, p] = await Promise.all([api.premiumBatches(), api.lookupValues("country"), api.premiumPricing()]); setBatches(b); setCountries(c.filter(v => v.isActive)); setPrices(p); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const countryName = (code?: string) => { const v = countries.find(c => c.code === code); return v ? label(v) : code; };
  const place = (code?: string, city?: string) => [city, countryName(code)].filter(Boolean).join(", ");
  async function move(b: PremiumBatch, status: "paid" | "cancelled") { setError(null); try { await api.setPremiumBatchStatus(b.id, status); await load(); } catch (e) { setError(errorMessage(e)); } }
  const pushed = (b: PremiumBatch) => (b.effectiveTo && b.effectiveTo > b.dateTo ? daysBetween(b.dateTo, b.effectiveTo) - 1 : 0);

  return (
    <main className="card full">
      <h1>Premium listings</h1>
      <p className="muted">A listing is premium on the days it holds a slot from a <b>paid</b> batch. Search shows premium listings first. Each country allows a limited number of premium listings per day (a batch for any of its cities counts against it). Batches are served in the order they were <b>paid</b>: a batch that finds a day full is pushed later, so it always receives exactly the days that were paid for; if a limit is lowered, the later payers move back and the first payers keep their days.</p>
      {error && <p className="error" role="alert">{error}</p>}

      {manage && <Booking countries={countries} onBooked={load} onError={setError} />}

      {paying && <PaymentForm title={`${paying.professionalName}, ${paying.dateFrom} to ${paying.dateTo}`} defaultBilledTo={paying.professionalName} amount={paying.totalFee} onCancel={() => setPaying(null)}
        onSubmit={async billing => { try { await api.setPremiumBatchStatus(paying.id, "paid", billing); setPaying(null); await load(); } catch (e) { await load(); throw e; } }} />}

      <h2>Batches</h2>
      <div className="table-wrap"><table className="t"><thead><tr><th>Professional</th><th>Place</th><th>Paid days</th><th>Runs</th><th>Fee / day</th><th>Total (USD)</th><th>Status</th><th>Receipt</th><th /></tr></thead><tbody>
        {batches.map(b => (
          <tr key={b.id}><td>{b.professionalName}<div className="muted small">{b.serviceName}</div></td><td>{place(b.countryCode, b.city)}</td>
            <td>{b.entitledDays} ({b.dateFrom} → {b.dateTo})</td>
            <td>{b.status === "paid" ? <>{b.daysAssigned} of {b.entitledDays} days, until {b.effectiveTo ?? "—"}{pushed(b) > 0 && <span className="pill" title="The country limit was reached, so this batch was pushed later"> pushed +{pushed(b)} days</span>} {b.activeToday && <span className="pill ok">premium today</span>}</> : <span className="muted">—</span>}</td>
            <td>{b.feePerDay.toFixed(2)}</td><td>{b.totalFee.toFixed(2)}</td>
            <td><span className={`pill ${b.status === "paid" ? "ok" : b.status === "cancelled" ? "bad" : ""}`}>{b.status}</span></td>
            <td>{b.receiptId ? <Link to={`/payments/${b.receiptId}`}>{b.receiptNumber}</Link> : <span className="muted">—</span>}</td>
            <td className="row">{manage && b.status === "pending" && <button className="btn small" onClick={() => setPaying(b)}>Record payment</button>}{manage && b.status !== "cancelled" && <button className="btn small secondary" onClick={() => void move(b, "cancelled")}>Cancel</button>}</td></tr>
        ))}
        {batches.length === 0 && <tr><td colSpan={9} className="muted">No premium batches yet.</td></tr>}
      </tbody></table></div>

      <Prices countries={countries} rows={prices} manage={manage} onChanged={load} onError={setError} countryName={countryName} />
    </main>
  );
}

function Booking({ countries, onBooked, onError }: { countries: LookupValue[]; onBooked: () => Promise<void>; onError: (m: string | null) => void }) {
  const [search, setSearch] = useState("");
  const [found, setFound] = useState<PremiumCandidate[]>([]);
  const [picked, setPicked] = useState<PremiumCandidate | null>(null);
  const [from, setFrom] = useState(today());
  const [to, setTo] = useState(today());
  const [preview, setPreview] = useState<{ days: PremiumCapacityDay[] } | null>(null);
  const [price, setPrice] = useState<ResolvedPremiumPricing | null>(null);

  async function find() { onError(null); try { setFound(await api.premiumFind(search)); } catch (e) { onError(errorMessage(e)); } }
  async function check() {
    if (!picked?.country) return;
    onError(null); setPreview(null);
    try { const [days, p] = await Promise.all([api.premiumCapacity(picked.country, picked.city, from, to), api.resolvePremiumPricing(picked.country, picked.city)]); setPreview({ days }); setPrice(p); }
    catch (e) { onError(errorMessage(e)); }
  }
  async function reserve() { if (!picked) return; onError(null); try { await api.createPremiumBatch(picked.professionId, from, to); setPreview(null); setPicked(null); setFound([]); await onBooked(); } catch (e) { onError(errorMessage(e)); } }
  const full = useMemo(() => preview?.days.filter(d => d.used >= d.maxPremium || (d.maxCity !== undefined && d.cityUsed !== undefined && d.cityUsed >= d.maxCity)).length ?? 0, [preview]);
  const valid = !!picked?.country && to >= from && from >= today();
  const cname = (code?: string) => { const v = countries.find(c => c.code === code); return v ? label(v) : code ?? "no country"; };

  return (
    <section className="card" aria-label="Book premium">
      <h2>Book premium days</h2>
      <div className="toolbar"><input aria-label="Find a listing" placeholder="Name, service or city" value={search} onChange={e => setSearch(e.target.value)} onKeyDown={e => { if (e.key === "Enter") void find(); }} /><button className="btn secondary" onClick={() => void find()}>Find</button></div>
      {found.length > 0 && !picked && (
        <ul className="plain" aria-label="Listings found">{found.map(c => (
          <li key={c.professionId}><button className="link" onClick={() => { setPicked(c); setPreview(null); }}>{c.firstName} {c.lastName} · {c.serviceName} · {[c.city, cname(c.country)].filter(Boolean).join(", ")}</button>{c.isPremium && <span className="pill ok"> premium today</span>}</li>))}</ul>
      )}
      {picked && (
        <>
          <p>Listing: <b>{picked.firstName} {picked.lastName}</b>, {picked.serviceName}, {[picked.city, cname(picked.country)].filter(Boolean).join(", ")} <button className="link" onClick={() => { setPicked(null); setPreview(null); }}>change</button></p>
          {!picked.country && <p className="error">This listing has no country, so it cannot be booked for premium.</p>}
          <div className="toolbar">
            <label>From<input type="date" aria-label="Premium from" min={today()} value={from} onChange={e => { setFrom(e.target.value); if (e.target.value > to) setTo(e.target.value); setPreview(null); }} /></label>
            <label>To<input type="date" aria-label="Premium to" min={from} value={to} onChange={e => { setTo(e.target.value); setPreview(null); }} /></label>
            <button className="btn secondary" disabled={!valid} onClick={() => void check()}>Check the days</button>
            <button className="btn" disabled={!valid} onClick={() => void reserve()}>Reserve</button>
          </div>
          {preview && price && (
            <div data-testid="premium-preview">
              <p>{daysBetween(from, to)} day(s) at {price.dailyFee.toFixed(2)} USD = <b>{(price.dailyFee * daysBetween(from, to)).toFixed(2)} USD</b>. {full === 0 ? "Every day has a free premium slot." : <span>{full} day(s) are already full: this batch would be pushed later by that many days once it is paid (it still gets all the days paid for).</span>}</p>
              <div className="table-wrap"><table className="t"><thead><tr><th>Day</th><th>Premium in the country</th><th>Limit</th>{price.maxPerCity !== undefined && <th>In the city / limit</th>}<th /></tr></thead><tbody>
                {preview.days.map(d => (<tr key={d.day}><td>{d.day}</td><td>{d.used}</td><td>{d.maxPremium}</td>{price.maxPerCity !== undefined && <td>{d.cityUsed ?? 0} / {d.maxCity}</td>}<td>{d.used >= d.maxPremium ? <span className="pill bad">full</span> : <span className="pill ok">free</span>}</td></tr>))}
              </tbody></table></div>
            </div>
          )}
        </>
      )}
    </section>
  );
}

function Prices({ countries, rows, manage, onChanged, onError, countryName }: { countries: LookupValue[]; rows: PremiumPricing[]; manage: boolean; onChanged: () => Promise<void>; onError: (m: string | null) => void; countryName: (c?: string) => string | undefined }) {
  const [f, setF] = useState({ country: "", city: "", fee: "", max: "" });
  const [check, setCheck] = useState<string | null>(null);
  useEffect(() => { if (!f.country && countries[0]) setF(x => ({ ...x, country: countries.find(c => c.code === "LB")?.code ?? countries[0].code })); }, [countries, f.country]);
  async function save() { onError(null); try { await api.savePremiumPricing({ countryCode: f.country, city: f.city.trim() || undefined, dailyFee: f.fee === "" ? undefined : Number(f.fee), maxPremium: f.max === "" ? undefined : Number(f.max) }); setF({ ...f, fee: "", max: "" }); await onChanged(); } catch (e) { onError(errorMessage(e)); } }
  async function remove(id: number) { onError(null); try { await api.deletePremiumPricing(id); await onChanged(); } catch (e) { onError(errorMessage(e)); } }
  async function resolve() { try { const r = await api.resolvePremiumPricing(f.country, f.city.trim() || undefined); setCheck(`${countryName(f.country)}${f.city.trim() ? ", " + f.city.trim() : ""}: ${r.dailyFee.toFixed(2)} USD per day; at most ${r.maxPerCountry} premium listings per day in the country${r.maxPerCity !== undefined ? ` and ${r.maxPerCity} in this city` : ""}.`); } catch (e) { onError(errorMessage(e)); } }
  return (
    <section aria-label="Premium prices and limits">
      <h2>Prices and limits</h2>
      <p className="muted">A country row sets the fee and the number of premium listings allowed per day in that country. A city row can set its own fee and an extra limit for that city. Empty values are inherited (city, then country, then the defaults in Settings → Premium). Changing a limit applies at once: later payers may move back, first payers keep their days.</p>
      <div className="table-wrap"><table className="t"><thead><tr><th>Country</th><th>City</th><th>Fee per day</th><th>Premium per day</th><th /></tr></thead><tbody>
        {rows.map(r => (<tr key={r.id}><td>{countryName(r.countryCode)}</td><td>{r.city ?? <span className="muted">whole country</span>}</td><td>{r.dailyFee !== undefined ? r.dailyFee.toFixed(2) : <span className="muted">inherited</span>}</td><td>{r.maxPremium ?? <span className="muted">inherited</span>}</td>
          <td>{manage && <button className="btn small secondary" onClick={() => void remove(r.id)}>Remove</button>}</td></tr>))}
        {rows.length === 0 && <tr><td colSpan={5} className="muted">Nothing set: the defaults in Settings apply everywhere.</td></tr>}
      </tbody></table></div>
      <div className="toolbar">
        <select aria-label="Premium country" value={f.country} onChange={e => setF({ ...f, country: e.target.value })}>{countries.map(c => <option key={c.code} value={c.code}>{label(c)}</option>)}</select>
        <input aria-label="Premium city" placeholder="City (optional)" value={f.city} onChange={e => setF({ ...f, city: e.target.value })} />
        <input aria-label="Premium fee per day" type="number" min="0" step="0.01" placeholder="Fee per day" value={f.fee} onChange={e => setF({ ...f, fee: e.target.value })} />
        <input aria-label="Premium per day" type="number" min="1" step="1" placeholder="Premium per day" value={f.max} onChange={e => setF({ ...f, max: e.target.value })} />
        {manage && <button className="btn" disabled={!f.country || (f.fee === "" && f.max === "")} onClick={() => void save()}>Save</button>}
        <button className="btn secondary" disabled={!f.country} onClick={() => void resolve()}>What applies here?</button>
      </div>
      {check && <p role="status" data-testid="premium-price-check">{check}</p>}
    </section>
  );
}
