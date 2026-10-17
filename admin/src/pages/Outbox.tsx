import { useCallback, useEffect, useState } from "react";
import { api, errorMessage, type OutboxRow, type Paged } from "../api";

import { formatWhen } from "../time";
import { t } from "../texts";
import { tn, statusLabel } from "../i18n";
import { tx } from "../i18n";
const STATUSES = ["", "queued", "sent", "failed", "skipped"];

/** Which emails were sent, waiting or failed. The content is never shown (it may contain sign-in style links). */
export default function Outbox() {
  const [status, setStatus] = useState("");
  const [page, setPage] = useState(1);
  const [data, setData] = useState<Paged<OutboxRow> | null>(null);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(async () => { try { setData(await api.emailOutbox(status, page)); setError(null); } catch (e) { setError(errorMessage(e)); } }, [status, page]);
  useEffect(() => { void load(); }, [load]);
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <main className="card full">
      <h1>{t("Email log")}</h1>
      <p className="muted">{tx("Every email the site tried to send. A message that fails is retried automatically; one marked {failed} used all its attempts (retry it from Background jobs once the mail server is fixed). The text of an email is removed as soon as it is sent.", { failed: <b>{t("failed")}</b> })}</p>
      <div className="toolbar"><select aria-label={t("Status")} value={status} onChange={e => { setStatus(e.target.value); setPage(1); }}>{STATUSES.map(s => <option key={s} value={s}>{s ? statusLabel(s) : t("Any status")}</option>)}</select><button className="btn secondary" onClick={() => void load()}>{t("Refresh")}</button></div>
      {error && <p className="error" role="alert">{error}</p>}
      {data && (<>
        <div className="table-wrap"><table className="t"><thead><tr><th>{t("When")}</th><th>{t("To")}</th><th>{t("About")}</th><th>{t("Subject")}</th><th>{t("Status")}</th><th>{t("Attempts")}</th><th>{t("Problem")}</th></tr></thead><tbody>
          {data.items.map(m => (<tr key={m.id}><td>{formatWhen(m.createdAt)}</td><td>{m.to}</td><td><code>{m.eventCode}</code></td><td>{m.subject}</td>
            <td><span className={`pill ${m.status === "failed" ? "bad" : m.status === "sent" ? "ok" : ""}`}>{statusLabel(m.status)}</span></td><td>{m.attempts}</td><td>{m.lastError ?? <span className="muted">—</span>}</td></tr>))}
          {data.items.length === 0 && <tr><td colSpan={7} className="muted">{t("No emails.")}</td></tr>}
        </tbody></table></div>
        <p className="row spread"><span className="muted">{tn(data.total, "1 email", "{n} emails")}</span>
          <span className="row"><button className="btn small secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>{t("Previous")}</button><span>{t("Page {page} of {pages}", { page, pages })}</span><button className="btn small secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>{t("Next")}</button></span></p>
      </>)}
    </main>
  );
}
