import { useEffect, useRef, useState } from "react";
import { api, type ServedAd } from "./api";
import { useConfig } from "./config";
import { usePlace } from "./place";

import { t } from "./texts";
const SEEN_FOR_MS = 1000;

/**
 * One advertising slot. Shows the ads running for this position and the visitor's place, rotating every Ads.RotationSeconds (paused while the mouse or
 * keyboard focus is on it, and not at all for visitors who prefer reduced motion). An ad counts as seen only after it has been at least half on screen,
 * in a visible tab, for a full second; each ad is reported once per page view. Renders nothing at all when there is nothing to show.
 */
export function AdSlot({ position }: { position: "home_banner" | "search_banner" | "sidebar" }) {
  const { country, city } = usePlace();
  const cfg = useConfig();
  const seconds = Math.max(cfg.num("Ads.RotationSeconds") || 5, 2);
  const [ads, setAds] = useState<ServedAd[]>([]);
  const [index, setIndex] = useState(0);
  const [paused, setPaused] = useState(false);
  const [onScreen, setOnScreen] = useState(false);
  const box = useRef<HTMLElement>(null);
  const reported = useRef(new Set<number>());

  useEffect(() => {
    let live = true;
    api.ads(position, country, city).then(a => { if (live) { setAds(a); setIndex(0); } }).catch(() => { if (live) setAds([]); });
    return () => { live = false; };
  }, [position, country, city]);

  const reduced = typeof window !== "undefined" && window.matchMedia?.("(prefers-reduced-motion: reduce)").matches;
  useEffect(() => {
    if (ads.length < 2 || paused || reduced) return;
    const t = setInterval(() => setIndex(i => (i + 1) % ads.length), seconds * 1000);
    return () => clearInterval(t);
  }, [ads.length, paused, reduced, seconds]);

  useEffect(() => {
    const el = box.current;
    if (!el || typeof IntersectionObserver === "undefined") { setOnScreen(true); return; }
    const io = new IntersectionObserver(entries => setOnScreen(entries.some(e => e.isIntersecting && e.intersectionRatio >= 0.5)), { threshold: [0, 0.5, 1] });
    io.observe(el);
    return () => io.disconnect();
  }, [ads.length]);

  const current = ads[index % Math.max(ads.length, 1)];
  useEffect(() => {
    if (!current || !onScreen || reported.current.has(current.id)) return;
    const timer = setTimeout(() => {
      if (document.visibilityState !== "visible" || reported.current.has(current.id)) return;
      reported.current.add(current.id);
      api.adImpression(current.id);
    }, SEEN_FOR_MS);
    return () => clearTimeout(timer);   // it left the screen, or the next ad took over, before a second passed
  }, [current, onScreen]);

  if (!current) return null;
  return (
    <aside ref={box} className={`ad-slot ad-${position}`} aria-label={t("Advertisement")} onMouseEnter={() => setPaused(true)} onMouseLeave={() => setPaused(false)} onFocus={() => setPaused(true)} onBlur={() => setPaused(false)}>
      <a href={current.clickUrl} target="_blank" rel="noopener noreferrer sponsored" data-ad-id={current.id}>
        <img src={current.imageUrl} alt={current.title} loading="lazy" />
      </a>
      <span className="ad-label">{t("Ad")}</span>
      {ads.length > 1 && (
        <div className="ad-dots" role="group" aria-label={t("Choose an ad")}>
          {ads.map((a, i) => <button key={a.id} type="button" className={i === index % ads.length ? "on" : ""} aria-label={t("Ad {n} of {total}", { n: i + 1, total: ads.length })} aria-current={i === index % ads.length} onClick={() => setIndex(i)} />)}
        </div>
      )}
    </aside>
  );
}
