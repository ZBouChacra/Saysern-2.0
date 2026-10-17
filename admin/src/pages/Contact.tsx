import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type ContactMessage, type ContactRow, type Paged } from "../api";
import { useSession } from "../session";

import { formatWhen } from "../time";
import { t } from "../texts";
import { tn, statusLabel } from "../i18n";
const STATUSES = ["all", "new", "in_progress", "replied", "archived", "spam"];
const when = (iso?: string) => (iso ? formatWhen(iso) : "");

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
      <h1>{t("Contact messages")}</h1>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="toolbar">
        <input aria-label={t("Search messages")} placeholder={t("Name, email or text")} value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <select aria-label={t("Message status")} value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}>{STATUSES.map(s => <option key={s} value={s}>{statusLabel(s)}</option>)}</select>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>{t("Received")}</th><th>{t("From")}</th><th>{t("Subject")}</th><th>{t("Status")}</th><th /></tr></thead><tbody>
          {data.items.map(m => (
            <tr key={m.id}><td>{when(m.createdAt)}</td><td>{m.name}<div className="muted small">{m.email}</div></td><td><b>{m.subject}</b><div className="muted small">{m.preview}</div></td>
              <td><span className={`pill ${m.status === "new" ? "ok" : m.status === "spam" ? "bad" : ""}`}>{statusLabel(m.status)}</span></td><td><button className="btn small secondary" onClick={() => void show(m.id)}>{t("Open")}</button></td></tr>
          ))}
          {data.items.length === 0 && <tr><td colSpan={5} className="muted">{t("No messages.")}</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{tn(data.total, "1 message", "{n} messages")}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("Previous")}</button><span>{t("Page {page} of {pages}", { page, pages })}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("Next")}</button></span></p>
      </>)}

      {open && (
        <section className="card" aria-label={t("Message")} data-testid="contact-detail">
          <div className="row spread"><h2>{open.subject}</h2><button className="btn small secondary" onClick={() => setOpen(null)}>{t("Close")}</button></div>
          <p className="muted">{t("From")}{" "}<b>{open.name}</b>{" "}{t("({email}) · {when} · language {language}", { email: open.email, when: when(open.createdAt), language: open.language })}{open.topicName ? ` · ${open.topicName}` : ""}{open.userId ? ` · account #${open.userId}` : ""}</p>
          <p className="pre">{open.body}</p>
          {open.replyBody && <div className="reply-box"><strong>{t("Replied {when}", { when: when(open.repliedAt) })}{open.repliedByName ? ` by ${open.repliedByName}` : ""}</strong><p className="pre">{open.replyBody}</p></div>}
          {manage && (<>
            <label>{t("Reply (sent by email to {email})", { email: open.email })}<textarea rows={5} value={reply} onChange={e => setReply(e.target.value)} /></label>
            <div className="row"><button className="btn" disabled={!reply.trim()} onClick={() => void send()}>{t("Send reply")}</button></div>
            <label>{t("Internal note (not sent)")}<input value={note} maxLength={1000} onChange={e => setNote(e.target.value)} /></label>
            <div className="row">{["in_progress", "archived", "spam", "new"].filter(s => s !== open.status).map(s => <button key={s} className="btn small secondary" onClick={() => void move(s)}>{t("Mark {replace}", { replace: statusLabel(s) })}</button>)}</div>
          </>)}
        </section>
      )}
    </main>
  );
}
