import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AdminReview, type Paged } from "../api";
import { useSession } from "../session";

import { formatDay } from "../time";
import { t } from "../texts";
import { tn } from "../i18n";
const stars = (n: number) => "★".repeat(n) + "☆".repeat(5 - n);

/** Every review, including hidden ones. Hiding needs a reason (kept in the audit log) and takes the review out of the professional's rating; it can be restored. */
export default function Reviews() {
  const { can } = useSession();
  const manage = can("reviews.manage");
  const [status, setStatus] = useState("all");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<AdminReview> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [hiding, setHiding] = useState<number | null>(null);
  const [reason, setReason] = useState("");
  const load = useCallback(async () => { try { setData(await api.adminReviews(status, search, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [status, search, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  async function hide(id: number) { try { await api.hideReview(id, reason); setHiding(null); setReason(""); await load(); } catch (e) { setError(errorMessage(e)); } }
  async function restore(id: number) { try { await api.restoreReview(id); await load(); } catch (e) { setError(errorMessage(e)); } }

  return (
    <main className="card full">
      <h1>{t("Reviews")}</h1>
      <p className="muted">{t("New reviews are published at once. Hide one that breaks the rules: it disappears from the site and from the rating, nothing is erased, and you can restore it. A professional can answer a review but cannot remove it.")}</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="toolbar">
        <input aria-label={t("Search reviews")} placeholder={t("Search text or names")} value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <select aria-label={t("Review status")} value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}><option value="all">{t("All")}</option><option value="visible">{t("Visible")}</option><option value="hidden">{t("Hidden")}</option></select>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>{t("When")}</th><th>{t("Reviewer → professional")}</th><th>{t("Rating")}</th><th>{t("Review and reply")}</th><th>{t("Status")}</th><th /></tr></thead><tbody>
          {data.items.map(r => (
            <tr key={r.id}><td>{formatDay(r.createdAt)}</td><td>{r.reviewerName} → {r.professionalName}</td><td title={`${r.rating} of 5`}>{stars(r.rating)}</td>
              <td>{r.comment ?? <span className="muted">{t("(no text)")}</span>}{r.replyText && <div className="muted small">{t("Reply: {replyText}", { replyText: r.replyText })}</div>}</td>
              <td>{r.isDeleted ? <><span className="pill bad">{t("hidden")}</span><div className="muted small">{r.deleteReason}{r.deletedByName ? ` · ${r.deletedByName}` : ""}</div></> : <span className="pill ok">{t("visible")}</span>}</td>
              <td>{manage && (hiding === r.id
                ? <span className="row"><input aria-label={t("Reason for hiding")} placeholder={t("Reason")} value={reason} onChange={e => setReason(e.target.value)} /><button className="btn small" disabled={!reason.trim()} onClick={() => void hide(r.id)}>{t("Hide")}</button><button className="btn small secondary" onClick={() => setHiding(null)}>{t("Cancel")}</button></span>
                : r.isDeleted ? <button className="btn small secondary" onClick={() => void restore(r.id)}>{t("Restore")}</button> : <button className="btn small secondary" onClick={() => { setHiding(r.id); setReason(""); }}>{t("Hide…")}</button>)}</td></tr>
          ))}
          {data.items.length === 0 && <tr><td colSpan={6} className="muted">{t("No reviews.")}</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{tn(data.total, "1 review", "{n} reviews")}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("Previous")}</button><span>{t("Page {page} of {pages}", { page, pages })}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("Next")}</button></span></p>
      </>)}
    </main>
  );
}
