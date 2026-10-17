import { existsSync, readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { uiKey } from "../texts";

// Every phrase the site shows through t("...") must have an Arabic translation, with the same {placeholders}. Adding a phrase without one fails here.
const dictionaryPath = join(process.cwd(), "..", "db", "tools", "ui_ar_web.json");
const PATTERN = /\b(?:t|tx|tl)\(\s*("(?:[^"\\\n]|\\.)*")/g;
const PLURAL = /\btn\(\s*[^,]+,\s*("(?:[^"\\\n]|\\.)*")\s*,\s*("(?:[^"\\\n]|\\.)*")/g;   // tn(n, "1 review", "{n} reviews")

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap(name => {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) return name === "test" || name === "node_modules" ? [] : sources(p);
    return /\.tsx?$/.test(name) && !/\.test\.tsx?$/.test(name) ? [p] : [];
  });
}
const placeholders = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort().join(",");

describe.skipIf(!existsSync(dictionaryPath))("the site's texts", () => {
  const dictionary = JSON.parse(readFileSync(dictionaryPath, "utf-8")) as Record<string, string>;
  const phrasesIn = (file: string): string[] => {
    const text = readFileSync(file, "utf-8");
    return [
      ...[...text.matchAll(PATTERN)].map(m => JSON.parse(m[1]) as string),
      ...[...text.matchAll(PLURAL)].flatMap(m => [JSON.parse(m[1]) as string, JSON.parse(m[2]) as string]),
    ];
  };
  const used = [...new Set(sources(join(process.cwd(), "src")).flatMap(phrasesIn))];

  it("have an Arabic translation for every phrase used in the code", () => {
    expect(used.filter(p => !(p in dictionary))).toEqual([]);
  });
  it("keep the same {placeholders} in Arabic as in English", () => {
    expect(used.filter(p => p in dictionary && placeholders(p) !== placeholders(dictionary[p]))).toEqual([]);
  });
  it("are stored under keys that fit the database, long phrases included", () => {
    expect(used.filter(p => uiKey(p).length > 140)).toEqual([]);
    expect(uiKey("short phrase")).toBe("ui:short phrase");
    // pinned against db/tools/ui_keys.py: the browser and the generator must agree on the key of a long phrase
    expect(uiKey("Long phrase for the key test. ".repeat(8))).toBe("ui:Long phrase for the key test. Long phrase for the key test. Long phrase for the key test. Long phrase for the …#498a0da5");
  });
  it("do not carry translations nobody uses any more", () => {
    expect(Object.keys(dictionary).filter(p => !used.includes(p))).toEqual([]);
  });
});
