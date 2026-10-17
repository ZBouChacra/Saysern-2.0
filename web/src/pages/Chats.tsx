import { useCallback, useEffect, useRef, useState, type ChangeEvent, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, errorMessage, type ChatMessage, type ChatRoomSummary } from "../api";
import { useAuth } from "../auth";
import { useConfig } from "../config";
import { fullName, when } from "../components";

import { t } from "../texts";
/** A file being sent: uploading, being compressed by the server (videos), or refused. */
interface Pending { key: string; name: string; fileId?: string; kind?: "image" | "video"; status: "uploading" | "processing" | "failed"; error?: string }

export default function Chats() {
  const { roomId } = useParams();
  const active = roomId ? Number(roomId) : null;
  const { user } = useAuth();
  const navigate = useNavigate();
  const [rooms, setRooms] = useState<ChatRoomSummary[]>([]);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);
  const bottom = useRef<HTMLDivElement>(null);
  const cfg = useConfig();
  const videoOn = cfg.bool("Media.VideoEnabled");
  const [pending, setPending] = useState<Pending[]>([]);

  const loadRooms = useCallback(() => { api.rooms().then(setRooms).catch(() => undefined); }, []);
  const loadMessages = useCallback(() => {
    if (active === null) return;
    api.messages(active).then(m => { setMessages([...m].reverse()); setError(null); loadRooms(); }).catch(e => setError(errorMessage(e)));
  }, [active, loadRooms]);

  useEffect(loadRooms, [loadRooms]);
  useEffect(() => { setMessages([]); loadMessages(); }, [loadMessages]);
  useEffect(() => { const t = setInterval(() => { loadMessages(); loadRooms(); }, 5000); return () => clearInterval(t); }, [loadMessages, loadRooms]);
  useEffect(() => { bottom.current?.scrollIntoView({ block: "end" }); }, [messages.length]);

  async function removeMessage(id: number) {
    if (active === null || !window.confirm(t("Delete this message for everyone?"))) return;
    try { await api.deleteMessage(active, id); loadMessages(); } catch (err) { setError(errorMessage(err)); }
  }
  async function removeConversation() {
    if (active === null || !window.confirm(t("Delete this conversation? It disappears from your list and your history is cleared. The other person keeps theirs."))) return;
    try { await api.deleteRoom(active); setMessages([]); loadRooms(); navigate("/chats"); } catch (err) { setError(errorMessage(err)); }
  }

  async function send(e: FormEvent) {
    e.preventDefault();
    if (active === null || !text.trim()) return;
    try { await api.send(active, { content: text.trim() }); setText(""); loadMessages(); } catch (err) { setError(errorMessage(err)); }
  }
  const patchPending = (key: string, patch: Partial<Pending>) => setPending(list => list.map(p => (p.key === key ? { ...p, ...patch } : p)));
  const dropPending = (key: string) => setPending(list => list.filter(p => p.key !== key));

  async function attach(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = "";
    if (!file || active === null) return;
    const key = `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    setPending(list => [...list, { key, name: file.name, status: "uploading" }]);
    try {
      const up = await api.uploadFile(file, "chat_media", active);
      if (up.status === "ready") { await api.send(active, { type: up.kind, fileId: up.id }); dropPending(key); loadMessages(); }
      else patchPending(key, { fileId: up.id, kind: up.kind, status: "processing" });   // a video: the server compresses it, we send it when it is ready
    } catch (err) { patchPending(key, { status: "failed", error: errorMessage(err) }); }
  }

  // while a video is being compressed, ask the server every couple of seconds; send it as soon as it is ready
  useEffect(() => {
    const waiting = pending.filter(p => p.status === "processing" && p.fileId);
    if (waiting.length === 0 || active === null) return;
    const timer = setInterval(async () => {
      for (const p of waiting) {
        try {
          const s = await api.fileStatus(p.fileId!);
          if (s.status === "ready") { await api.send(active, { type: p.kind, fileId: p.fileId }); dropPending(p.key); loadMessages(); }
          else if (s.status === "failed") patchPending(p.key, { status: "failed", error: errorMessage(s.failureCode ?? "PROCESSING_FAILED") });
        } catch { /* try again on the next tick */ }
      }
    }, 2000);
    return () => clearInterval(timer);
  }, [pending, active, loadMessages]);

  function shareLocation() {
    if (active === null || !navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(
      pos => { api.send(active, { type: "location", latitude: pos.coords.latitude, longitude: pos.coords.longitude }).then(loadMessages).catch(err => setError(errorMessage(err))); },
      () => setError(t("Couldn't get your location.")),
    );
  }

  return (
    <main className="card wide chat">
      <aside>
        <h2>{t("Messages")}</h2>
        {rooms.length === 0 && <p className="muted">{t("No conversations yet. Open a professional's page and choose Message.")}</p>}
        {rooms.map(r => (
          <button key={r.id} className={`room ${r.id === active ? "active" : ""}`} onClick={() => navigate(`/chats/${r.id}`)}>
            <strong>{fullName(r.otherFirstName, r.otherLastName)}</strong>{r.unreadCount > 0 && <span className="badge">{r.unreadCount}</span>}
            <div className="muted small">{r.lastMessageType === "location" ? t("📍 Location") : r.lastMessageType === "image" ? t("🖼 Photo") : r.lastMessageType === "video" ? t("🎬 Video") : r.lastMessageType === "deleted" ? t("Message deleted") : r.lastMessageContent ?? t("No messages yet")}</div>
          </button>
        ))}
      </aside>
      <section>
        {active === null ? <p className="muted">{t("Choose a conversation.")}</p> : (
          <>
            <div className="row spread no-pad"><span /><button className="link" onClick={() => void removeConversation()}>{t("Delete conversation")}</button></div>
            <div className="thread" aria-live="polite">
              {messages.map(m => (
                <div key={m.id} className={`bubble ${m.senderId === user?.id && !m.isStaff ? "mine" : ""} ${m.isStaff ? "staff" : ""} ${m.isDeleted ? "deleted" : ""}`}>
                  {m.isStaff && <div className="staff-label">{t("SaySerné team")}</div>}
                  {m.isDeleted ? <em className="muted">{t("This message was deleted")}</em> : <>
                  {m.messageType === "image" && m.fileId
                    ? <a href={`/api/files/${m.fileId}`} target="_blank" rel="noopener noreferrer"><img className="chat-media" src={`/api/files/${m.fileId}`} alt={t("Shared photo")} loading="lazy" /></a>
                    : m.messageType === "video" && m.fileId
                    ? <video className="chat-media" controls preload="metadata" src={`/api/files/${m.fileId}`} aria-label={t("Shared video")} />
                    : m.messageType === "location"
                    ? <a href={`https://www.openstreetmap.org/?mlat=${m.latitude}&mlon=${m.longitude}#map=16/${m.latitude}/${m.longitude}`} target="_blank" rel="noopener noreferrer">{t("📍 Shared location")}</a>
                    : m.content}
                  {(m.messageType === "image" || m.messageType === "video") && m.content && <div className="caption">{m.content}</div>}
                  </>}
                  <div className="muted small">{when(m.createdAt)}{m.senderId === user?.id && !m.isStaff && !m.isDeleted && <> · <button className="link" aria-label={t("Delete message")} onClick={() => void removeMessage(m.id)}>{t("Delete")}</button></>}</div>
                </div>
              ))}
              {pending.map(p => (
                <div key={p.key} className="bubble mine pending" role="status">
                  {p.status === "failed" ? <><span className="error">{p.name}: {p.error}</span> <button className="link" onClick={() => dropPending(p.key)}>{t("Dismiss")}</button></>
                    : p.status === "processing" ? t("Preparing {name}… (videos are compressed first)", { name: p.name }) : t("Uploading {name}…", { name: p.name })}
                </div>
              ))}
              <div ref={bottom} />
            </div>
            {error && <p className="error" role="alert">{error} <Link to="/chats">{t("Back")}</Link></p>}
            <form className="row composer" onSubmit={send}>
              <input value={text} onChange={e => setText(e.target.value)} placeholder={t("Type a message")} maxLength={4000} aria-label={t("Message")} />
              <label className="btn secondary file-btn" title={videoOn ? t("Send a photo or video") : t("Send a photo")}>📎
                <input type="file" hidden aria-label={t("Attach a file")} onChange={attach}
                  accept={"image/jpeg,image/png,image/gif,image/webp" + (videoOn ? ",video/mp4,video/quicktime,video/webm" : "")} /></label>
              <button type="button" className="btn secondary" onClick={shareLocation} title={t("Share my location")}>📍</button>
              <button className="btn" disabled={!text.trim()}>{t("Send")}</button>
            </form>
          </>
        )}
      </section>
    </main>
  );
}
