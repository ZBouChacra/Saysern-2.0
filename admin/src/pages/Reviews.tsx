import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AdminReview, type Paged } from "../api";
import { useSession } from "../session";

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
      <h1>Reviews</h1>
      <p className="muted">New reviews are published at once. Hide one that breaks the rules: it disappears from the site and from the rating, nothing is erased, and you can restore it. A professional can answer a review but cannot remove it.</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="toolbar">
        <input aria-label="Search reviews" placeholder="Search text or names" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <select aria-label="Review status" value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}><option value="all">All</option><option value="visible">Visible</option><option value="hidden">Hidden</option></select>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>When</th><th>Reviewer → professional</th><th>Rating</th><th>Review and reply</th><th>Status</th><th /></tr></thead><tbody>
          {data.items.map(r => (
            <tr key={r.id}><td>{new Date(r.createdAt).toLocaleDateString()}</td><td>{r.reviewerName} → {r.professionalName}</td><td title={`${r.rating} of 5`}>{stars(r.rating)}</td>
              <td>{r.comment ?? <span className="muted">(no text)</span>}{r.replyText && <div className="muted small">Reply: {r.replyText}</div>}</td>
              <td>{r.isDeleted ? <><span className="pill bad">hidden</span><div className="muted small">{r.deleteReason}{r.deletedByName ? ` · ${r.deletedByName}` : ""}</div></> : <span className="pill ok">visible</span>}</td>
              <td>{manage && (hiding === r.id
                ? <span className="row"><input aria-label="Reason for hiding" placeholder="Reason" value={reason} onChange={e => setReason(e.target.value)} /><button className="btn small" disabled={!reason.trim()} onClick={() => void hide(r.id)}>Hide</button><button className="btn small secondary" onClick={() => setHiding(null)}>Cancel</button></span>
                : r.isDeleted ? <button className="btn small secondary" onClick={() => void restore(r.id)}>Restore</button> : <button className="btn small secondary" onClick={() => { setHiding(r.id); setReason(""); }}>Hide…</button>)}</td></tr>
          ))}
          {data.items.length === 0 && <tr><td colSpan={6} className="muted">No reviews.</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{data.total} review{data.total === 1 ? "" : "s"}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
      </>)}
    </main>
  );
}
