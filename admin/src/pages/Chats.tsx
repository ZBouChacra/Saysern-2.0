import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type AdminChatRoom, type AdminChatThread, type Paged } from "../api";
import { useSession } from "../session";

const when = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "");

/** Moderation of conversations. Reading one is recorded in the audit log (who, which conversation, when). Staff can also write in it as the team. */
export default function Chats() {
  const { can } = useSession();
  const [filter, setFilter] = useState("all");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<AdminChatRoom> | null>(null);
  const [thread, setThread] = useState<AdminChatThread | null>(null);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { setData(await api.adminChats(filter, search, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [filter, search, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  async function open(id: number) { try { setThread(await api.readChat(id)); setError(null); } catch (e) { setError(errorMessage(e)); } }
  async function post() {
    if (!thread) return;
    try { await api.postStaffMessage(thread.room.id, text.trim()); setText(""); await open(thread.room.id); await load(); } catch (e) { setError(errorMessage(e)); }
  }
  const mediaUrl = (roomId: number, fileId: string) => `/api/admin/chats/${roomId}/files/${fileId}`;

  return (
    <main className="card full">
      <h1>Conversations</h1>
      <p className="muted">Private messages between users. <b>Opening a conversation is recorded in the audit log.</b> Deleted messages and chats stay visible here, with who deleted them.</p>
      {error && <p className="error" role="alert">{error}</p>}
      <div className="toolbar">
        <input aria-label="Search conversations" placeholder="Name or email" value={search} onChange={e => { setSearch(e.target.value); setPage(1); }} />
        <select aria-label="Conversation filter" value={filter} onChange={e => { setFilter(e.target.value); setPage(1); }}><option value="all">All conversations</option><option value="deleted">With deletions</option></select>
      </div>
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>People</th><th>Messages</th><th>Deleted</th><th>Last message</th><th /></tr></thead><tbody>
          {data.items.map(r => (
            <tr key={r.id}><td>{r.userAName} <span className="muted small">({r.userAEmail})</span><br />{r.userBName} <span className="muted small">({r.userBEmail})</span></td>
              <td>{r.messageCount}{r.staffMessages > 0 && <span className="pill"> {r.staffMessages} from the team</span>}</td>
              <td>{r.deletedMessages > 0 && <span className="pill bad">{r.deletedMessages} message{r.deletedMessages === 1 ? "" : "s"}</span>}{r.deletedByAAt && <span className="pill bad"> chat deleted by {r.userAName.split(" ")[0]}</span>}{r.deletedByBAt && <span className="pill bad"> chat deleted by {r.userBName.split(" ")[0]}</span>}</td>
              <td>{when(r.lastMessageAt)}</td><td><button className="btn small secondary" onClick={() => void open(r.id)}>Open</button></td></tr>
          ))}
          {data.items.length === 0 && <tr><td colSpan={5} className="muted">No conversations.</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{data.total} conversation{data.total === 1 ? "" : "s"}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button><span>Page {page} of {pages}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</button></span></p>
      </>)}

      {thread && (
        <section className="card" aria-label="Conversation" data-testid="chat-thread">
          <div className="row spread"><h2>{thread.room.userAName} and {thread.room.userBName}</h2><button className="btn small secondary" onClick={() => setThread(null)}>Close</button></div>
          <div className="thread-admin">
            {[...thread.messages].reverse().map(m => (
              <div key={m.id} className={`msg ${m.isStaff ? "staff" : ""} ${m.deletedAt ? "deleted" : ""}`}>
                <div className="muted small"><b>{m.isStaff ? "SaySerné team" : m.senderName}</b> · {when(m.createdAt)}{m.deletedAt && <span className="pill bad"> deleted by {m.deletedByName} · {when(m.deletedAt)}</span>}</div>
                {m.messageType === "image" && m.fileId ? <a href={mediaUrl(thread.room.id, m.fileId)} target="_blank" rel="noopener noreferrer"><img src={mediaUrl(thread.room.id, m.fileId)} alt="Shared photo" style={{ maxWidth: 240, maxHeight: 200 }} /></a>
                  : m.messageType === "video" && m.fileId ? <video controls preload="metadata" src={mediaUrl(thread.room.id, m.fileId)} style={{ maxWidth: 280 }} />
                  : m.messageType === "location" ? <span>📍 Location {m.latitude}, {m.longitude}</span> : null}
                {m.content && <div className={m.deletedAt ? "struck" : ""}>{m.content}</div>}
              </div>
            ))}
          </div>
          {can("chats.post") && (
            <div>
              <label>Write as the SaySerné team<textarea rows={2} maxLength={4000} value={text} onChange={e => setText(e.target.value)} /></label>
              <p className="muted small">Both people see it highlighted as the team's message and are told by email. It is recorded in the audit log.</p>
              <button className="btn" disabled={!text.trim()} onClick={() => void post()}>Send as the team</button>
            </div>
          )}
        </section>
      )}
    </main>
  );
}
