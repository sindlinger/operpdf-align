#!/usr/bin/env python3
"""Generate typed/anonymized model PDFs for despacho/certidao/requerimento.

Goal:
- keep document-style text flow
- remove real names and real numbers
- use placeholders (AAAAA/BBBBB/PPPPP/CCCCCCCC/000...)
"""

from __future__ import annotations

import argparse
import re
import subprocess
from pathlib import Path
from typing import Iterable

from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas


REPO_ROOT = Path(__file__).resolve().parent.parent

TARGET_GLOBS = [
    "models/aliases/despacho/*.pdf",
    "models/aliases/despacho_merged/*.pdf",
    "models/aliases/despacho_strict/*.pdf",
    "models/aliases/certidao/*.pdf",
    "models/aliases/requerimento/*.pdf",
]

EXCLUDE_SUFFIXES: tuple[str, ...] = ()

# Explicit known entities from current model corpus.
EXPLICIT_REPLACEMENTS = [
    (r"Christine Maria Batista de Brito Lyra", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Claudia Cristina Studart Leal", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Mara do Socorro da Silva", "AAAAA AAAAAAA AAAAA AA"),
    (r"Francisca Maria da Silva", "BBBBB BBBBBBB BBBBB BB"),
    (r"Felipe Queiroga Gadelha", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Robson de Lima Canan[eé]a", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Aline Santos Soares", "AAAAA AAAAAAA AAAAA AA"),
    (r"ALINE SANTOS SOARES", "AAAAA AAAAAAA AAAAA AA"),
    (r"Luciano da Silva Costa", "BBBBB BBBBBBB BBBBB BB"),
    (r"LUCIANO DA SILVA COSTA", "BBBBB BBBBBBB BBBBB BB"),
    (r"Carmen Helen Agra de Brito", "PPPPP PPPPP PPPPP PPPPP"),
    (r"CARMEN HELEN AGRA DE BRITO", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Elvis Sangelis Dias Marinheiro", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Marta Liane de Almeida Ramalho Loureiro", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Renata da C[âa]mara Pires Belmont", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Jo[aã]o Benedito", "PPPPP PPPPP"),
    (r"Márcio Murilo", "PPPPP PPPPP"),
    (r"Manoel Fons[êe]ca Xavier", "PPPPP PPPPP PPPPP"),
    (r"Mamanguape", "CCCCCCCC"),
    (r"Pianc[oó]", "CCCCCCCC"),
    (r"Sousa", "CCCCCCCC"),
    (r"Pocinhos", "CCCCCCCC"),
    (r"Campina Grande", "CCCCCCCC"),
    (r"Jo[aã]o Pessoa", "CCCCCCCC"),
]

SENSITIVE_TOKEN_PATTERNS = [
    r"renata",
    r"c[âa]mara",
    r"pires",
    r"belmont",
    r"marta",
    r"liane",
    r"almeida",
    r"ramalho",
    r"loureiro",
    r"lyra",
    r"luciano",
    r"aline",
    r"carmen",
    r"agra",
    r"elvis",
    r"sangelis",
    r"marinheiro",
    r"robson",
    r"canan[eé]a",
    r"jo[aã]o",
    r"benedito",
    r"m[áa]rcio",
    r"murilo",
    r"manoel",
    r"xavier",
]

EMAIL_RE = re.compile(r"\b[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}\b")
CPF_RE = re.compile(r"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b")
CNPJ_RE = re.compile(r"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b")
PROC_JUD_RE = re.compile(r"\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b")
PROC_ADMIN_RE = re.compile(r"\b\d{4}\.\d{3}\.\d{3}\b")
DATE_RE = re.compile(r"\b\d{1,2}/\d{1,2}/\d{2,4}\b")
BIG_NUM_RE = re.compile(r"\b\d{5,}\b")


def _run_pdftotext_layout(pdf_path: Path) -> str:
    raw = subprocess.check_output(["pdftotext", "-layout", str(pdf_path), "-"], cwd=REPO_ROOT)
    return raw.decode("utf-8", errors="ignore")


def _replace_digits_keep_shape(text: str) -> str:
    return re.sub(r"\d", "0", text)


def _sanitize_label_line(s: str) -> str:
    # Keep legal style, anonymize payload after labels (inline, not only line-start).
    pairs = [
        (r"(?i)\brequerente\s*:\s*[^;,.]+", "Requerente: Juízo da 0ª Vara da Comarca de CCCCCCCC"),
        (r"(?i)\binteressad[oa]\s*:\s*[^;,.]+", "Interessada: PPPPP PPPPP PPPPP PPPPP - Perita Médica Neurologista"),
        (r"(?i)\bpromovente\s*:\s*[^;,.]+", "Promovente: AAAAA AAAAAAA AAAAA AA"),
        (r"(?i)\bpromovido\s*:\s*[^;,.]+", "Promovido: BBBBB BBBBBBB BBBBB BB"),
        (r"(?i)\bautor(?:\(es\))?\s*:\s*[^;,.]+", "Autor(es): AAAAA AAAAAAA AAAAA AA"),
        (r"(?i)\br[ée]u(?:\(s\))?\s*:\s*[^;,.]+", "Réu(s): BBBBB BBBBBBB BBBBB BB"),
        (r"(?i)\bmovido por\b[^;,.]*", "movido por AAAAA AAAAAAA AAAAA AA"),
        (r"(?i)\bem face de?\b[^;,.]*", "em face de BBBBB BBBBBBB BBBBB BB"),
        (r"(?i)\bperante o ju[ií]zo\b[^;,.]*", "perante o Juízo da 0ª Vara da Comarca de CCCCCCCC."),
        (r"(?i)\bem favor da perit[ao]\b[^;,.]*", "em favor da Perita Médica Neurologista PPPPP PPPPP PPPPP PPPPP,"),
    ]
    for pat, rep in pairs:
        s = re.sub(pat, rep, s)
    return s


def sanitize_line(raw_line: str) -> str:
    s = raw_line.rstrip("\n")
    if not s.strip():
        return ""

    for pat, rep in EXPLICIT_REPLACEMENTS:
        s = re.sub(pat, rep, s, flags=re.IGNORECASE)

    s = _sanitize_label_line(s)

    # Normalize sensitive structured data.
    s = EMAIL_RE.sub("ppppp@ppppp.ppp", s)
    s = CPF_RE.sub("000.000.000-00", s)
    s = CNPJ_RE.sub("00.000.000/0000-00", s)
    s = PROC_JUD_RE.sub("0000000-00.0000.0.00.0000", s)
    s = PROC_ADMIN_RE.sub("0000.000.000", s)
    s = DATE_RE.sub("00/00/0000", s)
    s = BIG_NUM_RE.sub(lambda m: "0" * len(m.group(0)), s)

    for tok in SENSITIVE_TOKEN_PATTERNS:
        s = re.sub(rf"(?i)\b{tok}\b", "PPPPP", s)

    # Keep process heading pattern stable.
    s = re.sub(r"(?i)Processo\s*n[ºo]\s*[\d\.\-\/]+", "Processo nº 0000.000.000", s)
    s = re.sub(r"(?i)Of[ií]cio\s*n[ºo]\s*[\d\.\-\/]+", "Ofício nº 00/0000", s)

    # Final hard mask for any remaining digits.
    s = _replace_digits_keep_shape(s)
    s = re.sub(r"\s{2,}", " ", s).strip()
    return s


def render_pages_to_pdf(pages: list[str], out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(out_path), pagesize=A4)
    width, height = A4
    _ = width
    margin_x = 28
    margin_top = 36
    line_h = 11

    for page_text in pages:
        y = height - margin_top
        c.setFont("Helvetica", 10)

        for raw_line in page_text.splitlines():
            line = sanitize_line(raw_line)
            if line:
                c.drawString(margin_x, y, line[:180])
            y -= line_h
            if y < 32:
                c.showPage()
                c.setFont("Helvetica", 10)
                y = height - margin_top

        c.showPage()

    c.save()


def iter_targets() -> Iterable[Path]:
    seen: set[Path] = set()
    for pat in TARGET_GLOBS:
        for p in sorted(REPO_ROOT.glob(pat)):
            if not p.is_file():
                continue
            name = p.name.lower()
            if any(name.endswith(sfx) for sfx in EXCLUDE_SUFFIXES):
                continue
            if p in seen:
                continue
            seen.add(p)
            yield p


def main() -> None:
    parser = argparse.ArgumentParser(description="Gera modelos tipados (sem nomes/números reais).")
    parser.add_argument("--dry-run", action="store_true", help="Só lista arquivos alvo.")
    args = parser.parse_args()

    targets = list(iter_targets())
    if args.dry_run:
        for t in targets:
            print(t)
        return

    for path in targets:
        text = _run_pdftotext_layout(path)
        pages = text.split("\f")
        if pages and not pages[-1].strip():
            pages = pages[:-1]
        render_pages_to_pdf(pages, path)
        print(f"typed: {path}")


if __name__ == "__main__":
    main()
