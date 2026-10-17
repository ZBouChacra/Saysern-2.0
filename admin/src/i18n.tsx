import { Fragment, type ReactNode } from "react";
import { t } from "./texts";

/** A translated sentence that contains elements (bold text, a link): each {name} is replaced by the given node, wherever the translation puts it. */
export function tx(english: string, parts: Record<string, ReactNode>): ReactNode {
  return t(english).split(/(\{\w+\})/).map((piece, i) => {
    const m = /^\{(\w+)\}$/.exec(piece);
    return m && m[1] in parts ? <Fragment key={i}>{parts[m[1]]}</Fragment> : piece;
  });
}

/** "1 review" / "{n} reviews": two whole phrases, so every language can use its own plural forms. */
export function tn(n: number, one: string, other: string): string { return n === 1 ? t(one) : t(other, { n }); }

/** A status code as a word in the current language (the code itself is what the server stores and the screens compare). */
export function statusLabel(code: string): string {
  switch (code) {
    case "pending": return t("pending"); case "approved": return t("approved"); case "completed": return t("completed"); case "cancelled": return t("cancelled");
    case "paid": return t("paid"); case "issued": return t("issued"); case "void": return t("void"); case "failed": return t("failed");
    case "sent": return t("sent"); case "queued": return t("queued"); case "skipped": return t("skipped"); case "done": return t("done"); case "running": return t("running");
    case "new": return t("new"); case "in_progress": return t("in progress"); case "replied": return t("replied"); case "archived": return t("archived"); case "spam": return t("spam");
    case "all": return t("All");
    default: return code.replace(/_/g, " ");
  }
}
