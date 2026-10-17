import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type ContactMessage, type ContactRow, type Paged } from "../api";
import { useSession } from "../session";

const STATUSES = ["all", "new", "in_progress", "replied", "archived", "spam"];
const when = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "");

/** The inbox of the public contact form. A reply is e-mailed to the address the person typed, in the language they wrote in. */
export default function Contact() {
  const { can } = useSession();
  const manage = can("contact.manage");
  const [status, setStatus] = useState("new");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<ContactRow> | null>(null);
  const [open, setOpen] = useState<ContactMessage | null>(null);
  const [reply, setReply] = useState("");
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { setData(await api.contactList(status, search, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [status, search, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  async function show(id: number) { try { const m = await api.contactGet(id); setOpen(m); setReply(""); setNote(m.internalNote ?? ""); setError(null); } catch (e) { setError(errorMessage(e)); } }
  async function send() { if (!open) return; try { setOpen(await api.contactReply(open.id, reply)); setReply(""); await load(); setError(null); } catch (e) { setError(errorMessage(e)); } }
  async function move(s: string) { if (!open) return; try { setOpen(await api.contactStatus(open.id, s, note)); await load(); setError(null); } catch (e) { setError(errorMessage(e)); } }

  return (
    <main className="card full">
      <h1>Contact messages</h1>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="toolbar">
        <input aria-label="Search messages" placeholder="Name, email or text" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <select aria-label="Message status" value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}>{STATUSES.map(s => <option key={s} value={s}>{s.replace("_", " ")}</option>)}</select>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>Received</th><th>From</th><th>Subject</th><th>Status</th><th /></tr></thead><tbody>
          {data.items.map(m => (
            <tr key={m.id}><td>{when(m.createdAt)}</td><td>{m.name}<div className="muted small">{m.email}</div></td><td><b>{m.subject}</b><div className="muted small">{m.preview}</div></td>
              <td><span className={`pill ${m.status === "new" ? "ok" : m.status === "spam" ? "bad" : ""}`}>{m.status.replace("_", " ")}</span></td><td><button className="btn small secondary" onClick={() => void show(m.id)}>Open</button></td></tr>
          ))}
          {data.items.length === 0 && <tr><td colSpan={5} className="muted">No messages.</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{data.total} message{data.total === 1 ? "" : "s"}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
      </>)}

      {open && (
        <section className="card" aria-label="Message" data-testid="contact-detail">
          <div className="row spread"><h2>{open.subject}</h2><button className="btn small secondary" onClick={() => setOpen(null)}>Close</button></div>
          <p className="muted">From <b>{open.name}</b> ({open.email}) · {when(open.createdAt)} · language {open.language}{open.topicName ? ` · ${open.topicName}` : ""}{open.userId ? ` · account #${open.userId}` : ""}</p>
          <p className="pre">{open.body}</p>
          {open.replyBody && <div className="reply-box"><strong>Replied {when(open.repliedAt)}{open.repliedByName ? ` by ${open.repliedByName}` : ""}</strong><p className="pre">{open.replyBody}</p></div>}
          {manage && (<>
            <label>Reply (sent by email to {open.email})<textarea rows={5} value={reply} onChange={e => setReply(e.target.value)} /></label>
            <div className="row"><button className="btn" disabled={!reply.trim()} onClick={() => void send()}>Send reply</button></div>
            <label>Internal note (not sent)<input value={note} maxLength={1000} onChange={e => setNote(e.target.value)} /></label>
            <div className="row">{["in_progress", "archived", "spam", "new"].filter(s => s !== open.status).map(s => <button key={s} className="btn small secondary" onClick={() => void move(s)}>Mark {s.replace("_", " ")}</button>)}</div>
          </>)}
        </section>
      )}
    </main>
  );
}
