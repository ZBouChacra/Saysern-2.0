import { Fragment } from "react";
import { getText, t } from "../texts";

/** Turns an admin-written page body into paragraphs, headings ("# ") and lists ("- "). Everything is rendered as text, never as HTML. */
export function renderBody(body: string) {
  return body.split(/\n{2,}/).map((block, i) => {
    const lines = block.split("\n").map(l => l.trimEnd()).filter(Boolean);
    if (lines.length === 0) return null;
    if (lines.every(l => l.startsWith("- "))) return <ul key={i}>{lines.map((l, j) => <li key={j}>{l.slice(2)}</li>)}</ul>;
    return (
      <Fragment key={i}>
        {lines.map((l, j) => (l.startsWith("# ") ? <h2 key={j}>{l.slice(2)}</h2> : l.startsWith("- ") ? <ul key={j}><li>{l.slice(2)}</li></ul> : <p key={j}>{l}</p>))}
      </Fragment>
    );
  });
}

export default function ContentPage({ page }: { page: "about" | "terms" | "privacy" }) {
  const title = getText(`page.${page}.title`);
  const body = getText(`page.${page}.body`);
  return (
    <main className="card content-page">
      <h1>{title ?? ""}</h1>
      {body ? renderBody(body) : <p className="muted">{t("This page has no content yet.")}</p>}
    </main>
  );
}
