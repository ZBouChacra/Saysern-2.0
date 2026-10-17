import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AdPricing, type LookupValue, type ResolvedPricing } from "../api";
import { useSession } from "../session";

import { t } from "../texts";
const label = (v: LookupValue) => v.names[document.documentElement.lang] ?? v.names.en ?? v.code;   // in the language of the screen

/** What an ad costs per day and how many may run on the same day, per position and place. The defaults for everywhere else are in Settings. */
export default function AdPrices() {
  const { can } = useSession();
  const manage = can("ads.manage");
  const [rows, setRows] = useState<AdPricing[]>([]);
  const [positions, setPositions] = useState<LookupValue[]>([]);
  const [countries, setCountries] = useState<LookupValue[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [f, setF] = useState({ position: "", country: "", city: "", fee: "", max: "" });
  const [check, setCheck] = useState<{ text: string } | null>(null);

  const load = useCallback(async () => {
    try { const [p, pos, c] = await Promise.all([api.adPricing(), api.lookupValues("ad_position"), api.lookupValues("country")]); setRows(p); setPositions(pos.filter(v => v.isActive)); setCountries(c.filter(v => v.isActive)); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);
  useEffect(() => { if (!f.position && positions[0]) setF(x => ({ ...x, position: positions[0].code })); }, [positions, f.position]);

  const posName = (code: string) => { const v = positions.find(p => p.code === code); return v ? label(v) : code; };
  const countryName = (code?: string) => { const v = countries.find(c => c.code === code); return v ? label(v) : code; };
  const place = (r: { countryCode?: string; city?: string }) => r.countryCode ? [r.city, countryName(r.countryCode)].filter(Boolean).join(", ") : "Everywhere";

  async function save() {
    setError(null);
    try { await api.saveAdPricing({ position: f.position, countryCode: f.country || undefined, city: f.country ? f.city.trim() || undefined : undefined, dailyFee: f.fee === "" ? undefined : Number(f.fee), maxAds: f.max === "" ? undefined : Number(f.max) }); setF({ ...f, fee: "", max: "" }); await load(); }
    catch (e) { setError(errorMessage(e)); }
  }
  async function remove(id: number) { try { await api.deleteAdPricing(id); await load(); } catch (e) { setError(errorMessage(e)); } }
  async function resolve() {
    try { const r: ResolvedPricing = await api.resolveAdPricing(f.position, f.country || undefined, f.country ? f.city.trim() || undefined : undefined); setCheck({ text: t("{place}: {fee} USD per day, at most {max} ads per day.", { place: `${posName(f.position)} · ${place({ countryCode: f.country || undefined, city: f.city.trim() || undefined })}`, fee: r.dailyFee.toFixed(2), max: r.maxAds }) }); }
    catch (e) { setError(errorMessage(e)); }
  }

  return (
    <main className="card full">
      <h1>{t("Ad prices and limits")}</h1>
      <p className="muted">{t("A row sets a daily fee (USD) and/or the most ads that may run on the same day for a position and a place. A value a row leaves empty is inherited: city row, then country row, then the worldwide row, then the defaults in Settings (Advertising). A per-ad fee overrides all of this. Changing a price never alters a batch that was already reserved.")}</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="table-wrap"><table className="t"><thead><tr><th>{t("Position")}</th><th>{t("Place")}</th><th>{t("Fee per day")}</th><th>{t("Ads per day")}</th><th /></tr></thead><tbody>
        {rows.map(r => (<tr key={r.id}><td>{posName(r.position)}</td><td>{place(r)}</td><td>{r.dailyFee !== undefined ? r.dailyFee.toFixed(2) : <span className="muted">{t("inherited")}</span>}</td><td>{r.maxAds ?? <span className="muted">{t("inherited")}</span>}</td>
          <td>{manage && <button className="btn small secondary" onClick={() => void remove(r.id)}>{t("Remove")}</button>}</td></tr>))}
        {rows.length === 0 && <tr><td colSpan={5} className="muted">{t("No prices set: the defaults in Settings apply everywhere.")}</td></tr>}
      </tbody></table></div>

      <h2>{t("Set or check a price")}</h2>
      <div className="toolbar">
        <select aria-label={t("Position")} value={f.position} onChange={e => setF({ ...f, position: e.target.value })}>{positions.map(p => <option key={p.code} value={p.code}>{label(p)}</option>)}</select>
        <select aria-label={t("Country")} value={f.country} onChange={e => setF({ ...f, country: e.target.value, city: "" })}><option value="">{t("Everywhere")}</option>{countries.map(c => <option key={c.code} value={c.code}>{label(c)}</option>)}</select>
        <input aria-label={t("City")} placeholder={t("City (optional)")} value={f.city} disabled={!f.country} onChange={e => setF({ ...f, city: e.target.value })} />
        <input aria-label={t("Fee per day")} type="number" min="0" step="0.01" placeholder={t("Fee per day")} value={f.fee} onChange={e => setF({ ...f, fee: e.target.value })} />
        <input aria-label={t("Ads per day")} type="number" min="1" step="1" placeholder={t("Ads per day")} value={f.max} onChange={e => setF({ ...f, max: e.target.value })} />
        {manage && <button className="btn" disabled={!f.position || (f.fee === "" && f.max === "")} onClick={() => void save()}>{t("Save")}</button>}
        <button className="btn secondary" disabled={!f.position} onClick={() => void resolve()}>{t("What applies here?")}</button>
      </div>
      {check && <p role="status" data-testid="price-check">{check.text}</p>}
    </main>
  );
}
