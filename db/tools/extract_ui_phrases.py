#!/usr/bin/env python3
"""Lists every English phrase passed to t("...") / tx("...") in a front-end source folder (the keys of the "ui:" texts).
usage: extract_ui_phrases.py <srcDir>   -> JSON array on stdout"""
import json, os, re, sys

PATTERN = re.compile(r'\b(?:t|tx|tl)\(\s*("(?:[^"\\\n]|\\.)*")')
PLURAL = re.compile(r'\btn\(\s*[^,]+,\s*("(?:[^"\\\n]|\\.)*")\s*,\s*("(?:[^"\\\n]|\\.)*")')   # tn(n, "1 review", "{n} reviews")

def phrases(root: str) -> list[str]:
    found: set[str] = set()
    for folder, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in ("node_modules", "dist", "test")]
        for name in files:
            if name.endswith((".ts", ".tsx")) and not name.endswith((".test.ts", ".test.tsx")):
                text = open(os.path.join(folder, name), encoding="utf-8").read()
                for m in PATTERN.finditer(text):
                    found.add(json.loads(m.group(1)))
                for m in PLURAL.finditer(text):
                    found.update((json.loads(m.group(1)), json.loads(m.group(2))))
    return sorted(found)

if __name__ == "__main__":
    print(json.dumps(phrases(sys.argv[1]), ensure_ascii=False, indent=1))
