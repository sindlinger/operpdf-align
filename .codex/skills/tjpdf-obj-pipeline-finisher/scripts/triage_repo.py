#!/usr/bin/env python3
"""
triage_repo.py

Purpose:
  Produce a deterministic, low-effort map of a TJPDF-style repository so an agent can
  find entrypoints and key modules (OBJ, alignrange, mapfields, regex fields, detectors).

Usage:
  python scripts/triage_repo.py /path/to/repo

Output:
  - locations of authoritative docs (OBJETOS_PIPELINE.md, ALIGN_RANGE.md, etc.)
  - candidate entrypoints (main scripts / CLIs)
  - keyword hits for OBJ/alignrange/mapfields
"""

from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, List, Tuple


DOC_FILENAMES = {
    "OBJETOS_PIPELINE.md",
    "OBJETOS.md",
    "ALIGN_RANGE.md",
    "FIELDS.md",
    "FLEXIBLE_ROADMAP.md",
}

ENTRYPOINT_HINTS = [
    r"if\s+__name__\s*==\s*['\"]__main__['\"]",
    r"\bargparse\b",
    r"\bclick\b",
    r"\btyper\b",
    r"\bmain\s*\(",
]

KEYWORDS = [
    "alignrange",
    "TextOps",
    "/Contents",
    "diffmatchpatch",
    "ValueFull",
    "op_range",
    "anchors",
    "mapfields",
    "simhash",
    "k-means",
    "kmeans",
    "bookmark",
]


TEXT_FILE_EXTS = {".py", ".md", ".txt", ".yaml", ".yml", ".json"}


@dataclass
class Hit:
    path: str
    kind: str
    detail: str


def iter_files(root: Path) -> Iterable[Path]:
    for p in root.rglob("*"):
        if p.is_file() and (p.suffix.lower() in TEXT_FILE_EXTS or p.name in DOC_FILENAMES):
            yield p


def safe_read(p: Path, limit: int = 250_000) -> str:
    try:
        data = p.read_text(encoding="utf-8", errors="ignore")
        return data[:limit]
    except Exception:
        return ""


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: python scripts/triage_repo.py /path/to/repo")
        return 2

    root = Path(sys.argv[1]).resolve()
    if not root.exists():
        print(f"ERROR: path does not exist: {root}")
        return 2

    hits: List[Hit] = []

    # 1) Filename-based doc discovery
    for p in root.rglob("*"):
        if p.is_file() and p.name in DOC_FILENAMES:
            hits.append(Hit(str(p), "doc", f"Found doc file {p.name}"))

    # 2) Content-based discovery
    for p in iter_files(root):
        text = safe_read(p)
        if not text:
            continue

        # entrypoint score
        entry_score = 0
        for pat in ENTRYPOINT_HINTS:
            if re.search(pat, text):
                entry_score += 1
        if entry_score >= 2 and p.suffix.lower() == ".py":
            hits.append(Hit(str(p), "entrypoint_candidate", f"Matched {entry_score} entrypoint hints"))

        # keyword hits (first match per keyword)
        for kw in KEYWORDS:
            if kw in text:
                hits.append(Hit(str(p), "keyword", f"Contains keyword: {kw}"))
                break

    # Print summary
    print(f"Repository root: {root}")
    print("")

    def section(title: str, kind: str) -> None:
        items = [h for h in hits if h.kind == kind]
        print(f"== {title} ({len(items)}) ==")
        for h in sorted(items, key=lambda x: x.path)[:200]:
            print(f"- {h.path} :: {h.detail}")
        print("")

    section("Authoritative docs", "doc")
    section("Entrypoint candidates", "entrypoint_candidate")
    section("Keyword hits (OBJ/alignrange/mapfields/etc.)", "keyword")

    print("Next step suggestions:")
    print("- Open the doc files listed above first; treat them as operational truth.")
    print("- Pick ONE entrypoint candidate and trace imports to find the true pipeline.")
    print("- Identify mapfields finalizer and alignrange input/output contracts.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
