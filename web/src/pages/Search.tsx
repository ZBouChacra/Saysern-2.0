import { AdSlot } from "../AdSlot";
import { useEffect, useMemo, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { LookupSelect } from "../components";
import { useLookup } from "../config";
import { api, errorMessage, fromQuery, toQuery, type Category, type Listing, type Paged, type SearchFilters, type Service } from "../api";
import { useAuth } from "../auth";
import { ListingCard } from "../components";

export default function Search() {
  const frequencies = useLookup("alert_frequency");
  const { user } = useAuth();
  const [params, setParams] = useSearchParams();
  const filters = useMemo(() => fromQuery(params), [params]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [services, setServices] = useState<Service[]>([]);
  const [draft, setDraft] = useState<SearchFilters>(filters);
  const [result, setResult] = useState<Paged<Listing> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);

  useEffect(() => { api.categories().then(setCategories).catch(() => undefined); }, []);
  useEffect(() => { api.services(draft.categoryId).then(setServices).catch(() => setServices([])); }, [draft.categoryId]);
  useEffect(() => { setDraft(filters); }, [filters]);
  useEffect(() => {
    setResult(null);
    api.search(filters).then(r => { setResult(r); setError(null); }).catch(e => setError(errorMessage(e)));
  }, [filters]);

  const num = (v: string) => (v === "" ? undefined : Number(v));
  const set = (patch: Partial<SearchFilters>) => setDraft(d => ({ ...d, ...patch }));
  const submit = (e: FormEvent) => { e.preventDefault(); setParams(toQuery({ ...draft, page: 1 })); };
  const go = (page: number) => setParams(toQuery({ ...filters, page }));

  async function saveAlert() {
    const name = window.prompt("Name this saved search");
    if (!name) return;
    try { await api.createAlert(name, { ...filters, page: undefined, pageSize: undefined }, frequencies.values[0]?.code ?? "daily"); setSaved("Saved. Find it under Alerts."); }
    catch (e) { setSaved(errorMessage(e)); }
  }

  const pages = result ? Math.max(1, Math.ceil(result.total / result.pageSize)) : 1;
  return (
    <main className="card wide">
      <AdSlot position="search_banner" />
      <h1>Find a professional</h1>
      <form onSubmit={submit} className="filters">
        <label>Category
          <select value={draft.categoryId ?? ""} onChange={e => set({ categoryId: num(e.target.value), serviceId: undefined })}>
            <option value="">Any</option>{categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select></label>
        <label>Service
          <select value={draft.serviceId ?? ""} onChange={e => set({ serviceId: num(e.target.value) })}>
            <option value="">Any</option>{services.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
          </select></label>
        <label>Country<LookupSelect type="country" value={draft.country} onChange={v => set({ country: v })} emptyLabel="Any" /></label>
        <label>City<input value={draft.city ?? ""} onChange={e => set({ city: e.target.value })} /></label>
        <label>Sex<LookupSelect type="sex" value={draft.sex} onChange={v => set({ sex: v })} emptyLabel="Any" /></label>
        <label>Nationality<LookupSelect type="nationality" value={draft.nationality} onChange={v => set({ nationality: v })} emptyLabel="Any" /></label>
        <label>Name<input value={draft.firstName ?? ""} onChange={e => set({ firstName: e.target.value })} /></label>
        <label>Min rating
          <select value={draft.minStars ?? ""} onChange={e => set({ minStars: num(e.target.value) })}>
            <option value="">Any</option>{[1, 2, 3, 4, 5].map(n => <option key={n} value={n}>{n}★+</option>)}
          </select></label>
        <label>Max $/hour<input type="number" min={0} value={draft.maxCost ?? ""} onChange={e => set({ maxCost: num(e.target.value) })} /></label>
        <label>Min years<input type="number" min={0} value={draft.minExperience ?? ""} onChange={e => set({ minExperience: num(e.target.value) })} /></label>
        <label className="check"><input type="checkbox" checked={draft.hasOffice === true} onChange={e => set({ hasOffice: e.target.checked ? true : undefined })} /> Has an office</label>
        <label className="check"><input type="checkbox" checked={draft.hasTeam === true} onChange={e => set({ hasTeam: e.target.checked ? true : undefined })} /> Has a team</label>
        <div className="row"><button className="btn">Search</button><button type="button" className="btn secondary" onClick={() => setParams({})}>Clear</button></div>
      </form>

      {error && <p className="error" role="alert">{error}</p>}
      {!result && !error && <p className="muted">Searching…</p>}
      {result && (
        <>
          <p className="row spread">
            <span className="muted">{result.total} professional{result.total === 1 ? "" : "s"} found</span>
            {user && <button className="btn small secondary" onClick={() => void saveAlert()}>Save this search</button>}
          </p>
          {saved && <p className="muted">{saved}</p>}
          <div className="with-sidebar">
            <div className="list">{result.items.map(l => <ListingCard key={l.professionId} l={l} />)}</div>
            <AdSlot position="sidebar" />
          </div>
          {result.items.length === 0 && <p className="muted">No professionals match. Try removing a filter.</p>}
          {!user && result.total > 0 && <p className="muted"><Link to="/login">Sign in</Link> to book, message or save this search.</p>}
          <p className="row spread">
            <button className="btn small secondary" disabled={result.page <= 1} onClick={() => go(result.page - 1)}>Previous</button>
            <span>Page {result.page} of {pages}</span>
            <button className="btn small secondary" disabled={result.page >= pages} onClick={() => go(result.page + 1)}>Next</button>
          </p>
        </>
      )}
    </main>
  );
}
