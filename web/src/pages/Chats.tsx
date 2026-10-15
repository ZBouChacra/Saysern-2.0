import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api, errorMessage, type ChatMessage, type ChatRoomSummary } from "../api";
import { useAuth } from "../auth";
import { fullName, when } from "../components";

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

  const loadRooms = useCallback(() => { api.rooms().then(setRooms).catch(() => undefined); }, []);
  const loadMessages = useCallback(() => {
    if (active === null) return;
    api.messages(active).then(m => { setMessages([...m].reverse()); setError(null); loadRooms(); }).catch(e => setError(errorMessage(e)));
  }, [active, loadRooms]);

  useEffect(loadRooms, [loadRooms]);
  useEffect(() => { setMessages([]); loadMessages(); }, [loadMessages]);
  useEffect(() => { const t = setInterval(() => { loadMessages(); loadRooms(); }, 5000); return () => clearInterval(t); }, [loadMessages, loadRooms]);
  useEffect(() => { bottom.current?.scrollIntoView({ block: "end" }); }, [messages.length]);

  async function send(e: FormEvent) {
    e.preventDefault();
    if (active === null || !text.trim()) return;
    try { await api.send(active, { content: text.trim() }); setText(""); loadMessages(); } catch (err) { setError(errorMessage(err)); }
  }
  function shareLocation() {
    if (active === null || !navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(
      pos => { api.send(active, { type: "location", latitude: pos.coords.latitude, longitude: pos.coords.longitude }).then(loadMessages).catch(err => setError(errorMessage(err))); },
      () => setError("Couldn't get your location."),
    );
  }

  return (
    <main className="card wide chat">
      <aside>
        <h2>Messages</h2>
        {rooms.length === 0 && <p className="muted">No conversations yet. Open a professional's page and choose Message.</p>}
        {rooms.map(r => (
          <button key={r.id} className={`room ${r.id === active ? "active" : ""}`} onClick={() => navigate(`/chats/${r.id}`)}>
            <strong>{fullName(r.otherFirstName, r.otherLastName)}</strong>{r.unreadCount > 0 && <span className="badge">{r.unreadCount}</span>}
            <div className="muted small">{r.lastMessageType === "location" ? "📍 Location" : r.lastMessageContent ?? "No messages yet"}</div>
          </button>
        ))}
      </aside>
      <section>
        {active === null ? <p className="muted">Choose a conversation.</p> : (
          <>
            <div className="thread" aria-live="polite">
              {messages.map(m => (
                <div key={m.id} className={`bubble ${m.senderId === user?.id ? "mine" : ""}`}>
                  {m.messageType === "location"
                    ? <a href={`https://www.openstreetmap.org/?mlat=${m.latitude}&mlon=${m.longitude}#map=16/${m.latitude}/${m.longitude}`} target="_blank" rel="noopener noreferrer">📍 Shared location</a>
                    : m.content}
                  <div className="muted small">{when(m.createdAt)}</div>
                </div>
              ))}
              <div ref={bottom} />
            </div>
            {error && <p className="error" role="alert">{error} <Link to="/chats">Back</Link></p>}
            <form className="row composer" onSubmit={send}>
              <input value={text} onChange={e => setText(e.target.value)} placeholder="Type a message" maxLength={4000} aria-label="Message" />
              <button type="button" className="btn secondary" onClick={shareLocation} title="Share my location">📍</button>
              <button className="btn" disabled={!text.trim()}>Send</button>
            </form>
          </>
        )}
      </section>
    </main>
  );
}
