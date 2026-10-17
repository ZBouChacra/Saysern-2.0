import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AuditRow, type Paged } from "../api";

import { formatWhen } from "../time";
import { t } from "../texts";
import { tn } from "../i18n";
export default function Audit() {
  const [f, setF] = useState({ search: "", action: "", entityType: "", actorId: "", from: "", to: "" });
  const [applied, setApplied] = useState(f);
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<AuditRow> | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => {
    try { setData(await api.audit({ ...applied, from: applied.from ? new Date(applied.from).toISOString() : undefined, to: applied.to ? new Date(applied.to + "T23:59:59").toISOString() : undefined, page, pageSize: 25 })); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }, [applied, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });

  return (
    <main className="card full">
      <h1>{t("Audit log")}</h1>
      <p className="muted">{t("Every back-office change, written in the same database transaction as the change itself. Entries can't be edited or deleted.")}</p>
      <form className="toolbar" onSubmit={e => { e.preventDefault(); setPage(1); setApplied(f); }}>
        <input placeholder={t("Search")} aria-label={t("Search audit")} value={f.search} onChange={set("search")} />
        <input placeholder={t("Action (e.g. settings.update)")} aria-label={t("Action")} value={f.action} onChange={set("action")} />
        <input placeholder={t("Entity (e.g. user)")} aria-label={t("Entity type")} value={f.entityType} onChange={set("entityType")} />
        <input placeholder={t("Actor id")} aria-label={t("Actor id")} value={f.actorId} onChange={set("actorId")} style={{ maxWidth: 110 }} />
        <input type="date" aria-label={t("From")} value={f.from} onChange={set("from")} /><input type="date" aria-label={t("To")} value={f.to} onChange={set("to")} />
        <button className="btn">{t("Filter")}</button>
      </form>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (
        <>
          <div className="table-wrap"><table className="t"><thead><tr><th>{t("When")}</th><th>{t("Who")}</th><th>{t("Action")}</th><th>{t("What")}</th><th /></tr></thead><tbody>
            {data.items.map(r => (
              <>
                <tr key={r.id}><td>{formatWhen(r.occurredAt)}</td><td>{r.actorName ?? <span className="muted">{t("system")}</span>}</td><td><code>{r.action}</code></td><td>{r.summary}</td>
                  <td>{r.details !== undefined && <button className="btn small secondary" onClick={() => setOpen(open === r.id ? null : r.id)}>{open === r.id ? t("Hide") : t("Details")}</button>}</td></tr>
                {open === r.id && <tr key={`${r.id}d`}><td colSpan={5}><pre className="json">{JSON.stringify(r.details, null, 2)}</pre>{r.ip && <span className="muted small">{t("IP {ip}", { ip: r.ip })}</span>}</td></tr>}
              </>
            ))}
            {data.items.length === 0 && <tr><td colSpan={5} className="muted">{t("No entries.")}</td></tr>}
          </tbody></table></div>
          <p className="row spread"><span className="muted">{tn(data.total, "1 entry", "{n} entries")}</span>
            <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("Previous")}</button><span>{t("Page {page} of {pages}", { page, pages })}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("Next")}</button></span></p>
        </>
      )}
    </main>
  );
}
