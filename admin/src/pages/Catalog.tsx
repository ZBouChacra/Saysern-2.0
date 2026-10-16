import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type Category, type Service } from "../api";

export default function Catalog() {
  const [categories, setCategories] = useState<Category[]>([]);
  const [selected, setSelected] = useState<Category | null>(null);
  const [services, setServices] = useState<Service[]>([]);
  const [catDraft, setCatDraft] = useState<(Omit<Category, "id"> & { id: number | null }) | null>(null);
  const [svcDraft, setSvcDraft] = useState<(Omit<Service, "id"> & { id: number | null }) | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadCategories = useCallback(async () => { try { setCategories(await api.categories()); } catch (e) { setError(errorMessage(e)); } }, []);
  useEffect(() => { void loadCategories(); }, [loadCategories]);
  useEffect(() => { if (selected) api.services(selected.id).then(setServices).catch(e => setError(errorMessage(e))); else setServices([]); }, [selected]);

  async function saveCategory() {
    if (!catDraft) return;
    try { const { id, ...body } = catDraft; const saved = await api.saveCategory(id, body); setCatDraft(null); setError(null); await loadCategories(); setSelected(saved); } catch (e) { setError(errorMessage(e)); }
  }
  async function saveService() {
    if (!svcDraft || !selected) return;
    try { const { id, ...body } = svcDraft; await api.saveService(id, { ...body, categoryId: selected.id }); setSvcDraft(null); setError(null); setServices(await api.services(selected.id)); } catch (e) { setError(errorMessage(e)); }
  }
  const blankCat = { id: null, name: "", nameAr: "", description: "", descriptionAr: "", icon: "", isBlocked: false };

  return (
    <main className="card full">
      <h1>Catalog</h1>
      <p className="muted">The categories and services professionals can list. Blocking one hides it from visitors and stops new listings; existing listings are untouched.</p>
      {error && !catDraft && !svcDraft && <p className="error" role="alert">{error}</p>}
      <div className="two">
        <section><div className="row spread"><h2>Categories</h2><button className="btn small" onClick={() => setCatDraft({ ...blankCat })}>Add</button></div>
          {categories.map(c => <button key={c.id} className={`room ${selected?.id === c.id ? "active" : ""}`} onClick={() => setSelected(c)}><b>{c.name}</b> {c.isBlocked && <span className="pill bad">blocked</span>}<div className="muted small" dir="rtl">{c.nameAr}</div></button>)}</section>
        <section>
          {!selected ? <p className="muted">Choose a category.</p> : (
            <>
              <div className="row spread"><h2>{selected.name}</h2><span className="row"><button className="btn small secondary" onClick={() => setCatDraft({ ...blankCat, ...selected })}>Edit category</button><button className="btn small" onClick={() => setSvcDraft({ categoryId: selected.id, name: "", nameAr: "", description: "", descriptionAr: "", isBlocked: false, id: null })}>Add service</button></span></div>
              <div className="table-wrap"><table className="t"><thead><tr><th>Service</th><th>Arabic</th><th>Status</th><th /></tr></thead><tbody>
                {services.map(s => <tr key={s.id}><td>{s.name}</td><td dir="rtl">{s.nameAr}</td><td>{s.isBlocked ? <span className="pill bad">blocked</span> : "active"}</td><td><button className="btn small secondary" onClick={() => setSvcDraft({ ...s })}>Edit</button></td></tr>)}
                {services.length === 0 && <tr><td colSpan={4} className="muted">No services yet.</td></tr>}
              </tbody></table></div>
            </>
          )}
        </section>
      </div>
      {catDraft && (
        <div className="modal" role="dialog" aria-label="Category"><div><h2>{catDraft.id === null ? "New category" : "Edit category"}</h2>
          <label>Name (English)<input value={catDraft.name} onChange={e => setCatDraft({ ...catDraft, name: e.target.value })} /></label>
          <label>Name (Arabic)<input dir="rtl" value={catDraft.nameAr ?? ""} onChange={e => setCatDraft({ ...catDraft, nameAr: e.target.value })} /></label>
          <label>Description<input value={catDraft.description ?? ""} onChange={e => setCatDraft({ ...catDraft, description: e.target.value })} /></label>
          <label>Icon<input value={catDraft.icon ?? ""} onChange={e => setCatDraft({ ...catDraft, icon: e.target.value })} /></label>
          <label className="chk"><input type="checkbox" checked={catDraft.isBlocked} onChange={e => setCatDraft({ ...catDraft, isBlocked: e.target.checked })} /> Blocked</label>
          {error && <p className="error" role="alert">{error}</p>}<div className="row"><button className="btn" onClick={() => void saveCategory()}>Save</button><button className="btn secondary" onClick={() => { setCatDraft(null); setError(null); }}>Cancel</button></div></div></div>
      )}
      {svcDraft && (
        <div className="modal" role="dialog" aria-label="Service"><div><h2>{svcDraft.id === null ? "New service" : "Edit service"}</h2>
          <label>Name (English)<input value={svcDraft.name} onChange={e => setSvcDraft({ ...svcDraft, name: e.target.value })} /></label>
          <label>Name (Arabic)<input dir="rtl" value={svcDraft.nameAr ?? ""} onChange={e => setSvcDraft({ ...svcDraft, nameAr: e.target.value })} /></label>
          <label className="chk"><input type="checkbox" checked={svcDraft.isBlocked} onChange={e => setSvcDraft({ ...svcDraft, isBlocked: e.target.checked })} /> Blocked</label>
          {error && <p className="error" role="alert">{error}</p>}<div className="row"><button className="btn" onClick={() => void saveService()}>Save</button><button className="btn secondary" onClick={() => { setSvcDraft(null); setError(null); }}>Cancel</button></div></div></div>
      )}
    </main>
  );
}
