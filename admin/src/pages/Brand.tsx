import { useCallback, useEffect, useState, type ChangeEvent } from "react";
import { api, errorMessage, type Setting } from "../api";
import { contrastWithWhite, isHex } from "../brand";
import { useSession } from "../session";
import { t } from "../texts";

const COLOURS = [
  { key: "Brand.PrimaryColor", label: () => t("Main colour (buttons and links)") },
  { key: "Brand.PrimaryDarkColor", label: () => t("Darker shade (hovering over buttons)") },
  { key: "Brand.AccentColor", label: () => t("Accent colour") },
] as const;

/** The look of the public site: three colours and a logo. A preview shows the result before it is saved. */
export default function Brand() {
  const { can } = useSession();
  const manage = can("settings.manage");
  const [rows, setRows] = useState<Setting[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [message, setMessage] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try { const all = (await api.settings()).filter(s => s.key.startsWith("Brand.")); setRows(all); setDraft(Object.fromEntries(all.map(s => [s.key, s.value]))); setError(null); }
    catch (e) { setError(errorMessage(e)); }
  }, []);
  useEffect(() => { void load(); }, [load]);

  const value = (key: string) => draft[key] ?? "";
  const dirty = COLOURS.some(c => value(c.key) !== (rows.find(r => r.key === c.key)?.value ?? ""));
  const allValid = COLOURS.every(c => isHex(value(c.key)));
  const primary = isHex(value("Brand.PrimaryColor")) ? value("Brand.PrimaryColor") : "#2d6d5f";
  const dark = isHex(value("Brand.PrimaryDarkColor")) ? value("Brand.PrimaryDarkColor") : "#1f4d43";
  const accent = isHex(value("Brand.AccentColor")) ? value("Brand.AccentColor") : "#d4a757";
  const contrast = contrastWithWhite(primary);
  const logoId = rows.find(r => r.key === "Brand.LogoFileId")?.value ?? "";

  async function saveColours() {
    setBusy(true); setMessage(null); setError(null);
    try { for (const c of COLOURS) if (value(c.key) !== rows.find(r => r.key === c.key)?.value) await api.setSetting(c.key, value(c.key)); await load(); setMessage(t("Saved. Visitors see the new colours within a few seconds of opening the site.")); }
    catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  }
  async function resetColours() {
    setBusy(true); setMessage(null); setError(null);
    try { for (const c of COLOURS) await api.resetSetting(c.key); await load(); setMessage(t("Back to the original colours.")); }
    catch (e) { setError(errorMessage(e)); } finally { setBusy(false); }
  }
  async function upload(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]; e.target.value = "";
    if (!file) return;
    setBusy(true); setMessage(null); setError(null);
    try { const { id } = await api.uploadLogo(file); await api.setSetting("Brand.LogoFileId", id); await load(); setMessage(t("The logo is set.")); }
    catch (err) { setError(errorMessage(err)); } finally { setBusy(false); }
  }
  async function removeLogo() {
    setBusy(true); setMessage(null); setError(null);
    try { await api.setSetting("Brand.LogoFileId", ""); await load(); setMessage(t("The logo was removed: the site name is shown instead.")); }
    catch (err) { setError(errorMessage(err)); } finally { setBusy(false); }
  }

  return (
    <main className="card full">
      <h1>{t("Brand")}</h1>
      <p className="muted">{t("The colours and logo of the public site. The site name itself is a setting (Site.Name).")}</p>
      {message && <p className="ok-text" role="status">{message}</p>}
      {error && <p className="error" role="alert">{error}</p>}

      <section aria-label={t("Colours")}>
        <h2>{t("Colours")}</h2>
        {COLOURS.map(c => (
          <div className="row" key={c.key}>
            <label>{c.label()}
              <span className="row">
                <input type="color" aria-label={c.label()} disabled={!manage} value={isHex(value(c.key)) ? value(c.key) : "#000000"} onChange={e => setDraft({ ...draft, [c.key]: e.target.value })} />
                <input aria-label={`${c.label()} (#rrggbb)`} disabled={!manage} maxLength={7} value={value(c.key)} onChange={e => setDraft({ ...draft, [c.key]: e.target.value })} />
              </span>
            </label>
            {!isHex(value(c.key)) && <span className="field-error" role="alert">{t("Use the form #rrggbb.")}</span>}
          </div>
        ))}
        {contrast < 4.5 && <p className="error" role="alert" data-testid="contrast-warning">{t("White text on the main colour has a contrast of only {ratio}:1. Choose a darker colour (4.5:1 or more is comfortable to read).", { ratio: contrast.toFixed(1) })}</p>}
        <div className="brand-preview" aria-label={t("Preview")} style={{ border: `1px solid ${dark}`, borderRadius: 10, padding: "1rem" }}>
          <strong style={{ color: primary }}>{t("Preview")}</strong>
          <p><span style={{ background: primary, color: "#fff", padding: ".4rem .9rem", borderRadius: 8, display: "inline-block" }}>{t("A button")}</span>{" "}
            <span style={{ background: dark, color: "#fff", padding: ".4rem .9rem", borderRadius: 8, display: "inline-block" }}>{t("When hovered")}</span>{" "}
            <span style={{ background: accent, color: "#222", padding: ".2rem .6rem", borderRadius: 999, display: "inline-block" }}>{t("An accent")}</span></p>
        </div>
        {manage && <div className="row"><button className="btn" disabled={busy || !dirty || !allValid} onClick={() => void saveColours()}>{t("Save colours")}</button><button className="btn secondary" disabled={busy} onClick={() => void resetColours()}>{t("Use the original colours")}</button></div>}
      </section>

      <section aria-label={t("Logo")}>
        <h2>{t("Logo")}</h2>
        {logoId ? <p><img src={`/api/files/${logoId}`} alt={t("The current logo")} style={{ maxHeight: 64, maxWidth: 240, objectFit: "contain", background: "#fff", padding: 6, border: "1px solid var(--line)", borderRadius: 8 }} /></p>
          : <p className="muted">{t("No logo: the site name is shown in the header.")}</p>}
        {manage && (
          <div className="row">
            <label className="btn secondary">{logoId ? t("Change logo") : t("Upload logo")}<input type="file" accept="image/png,image/jpeg,image/webp" hidden disabled={busy} onChange={e => void upload(e)} /></label>
            {logoId && <button className="btn secondary" disabled={busy} onClick={() => void removeLogo()}>{t("Remove logo")}</button>}
          </div>
        )}
        <p className="muted small">{t("PNG, JPEG or WebP. Shown at a height of about 32 pixels, so a wide, simple logo works best. Pictures are stored as JPEG: transparent areas become white.")}</p>
      </section>
    </main>
  );
}
