import { Fragment, type ReactNode } from "react";
import { t } from "./texts";

/** A translated sentence that contains elements (a link, bold text): each {name} is replaced by the given node, wherever the translation puts it. */
export function tx(english: string, parts: Record<string, ReactNode>): ReactNode {
  return t(english).split(/(\{\w+\})/).map((piece, i) => {
    const m = /^\{(\w+)\}$/.exec(piece);
    return m && m[1] in parts ? <Fragment key={i}>{parts[m[1]]}</Fragment> : piece;
  });
}

/** The password rules as one whole sentence (so a translation can order the words naturally). */
export function passwordHint(min: number, letter: boolean, digit: boolean): string {
  if (letter && digit) return t("At least {min} characters, with a letter and a digit.", { min });
  if (letter) return t("At least {min} characters, with a letter.", { min });
  if (digit) return t("At least {min} characters, with a digit.", { min });
  return t("At least {min} characters.", { min });
}

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
