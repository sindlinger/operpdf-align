#!/usr/bin/env python3
"""Generate typed/anonymized despacho model PDFs from source PDFs.

This keeps document-style flow (full text pages) while removing sensitive data.
"""

from __future__ import annotations

import re
import subprocess
from pathlib import Path

from reportlab.lib.pagesizes import A4
from reportlab.pdfgen import canvas


REPO_ROOT = Path(__file__).resolve().parent.parent

SOURCE_TARGETS = [
    (
        REPO_ROOT / "models/aliases/despacho_strict/tjpb_despacho_model.backup.pdf",
        REPO_ROOT / "models/aliases/despacho_strict/tjpb_despacho_model.pdf",
    ),
]

# Strong explicit replacements for known entities from real fixtures.
REPLACEMENTS = [
    (r"Christine Maria Batista de Brito Lyra", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Claudia Cristina Studart Leal", "PPPPP PPPPP PPPPP PPPPP"),
    (r"Mara do Socorro da Silva", "AAAAA AAAAAAA AAAAA AA"),
    (r"Francisca Maria da Silva", "BBBBB BBBBBBB BBBBB BB"),
    (r"JOÃO BATISTA DE OLIVEIRA", "AAAAA AAAAAAA AAAAA AA"),
    (r"João Batista de Oliveira", "AAAAA AAAAAAA AAAAA AA"),
    (r"INSTITUTO NACIONAL DO SEGURO SOCIAL", "BBBBB BBBBBBB BBBBB BB"),
    (r"Robson de Lima Canan[eé]a", "PPPPP PPPPP PPP PPPPPPP"),
    (r"Mamanguape", "CCCCCCCC"),
    (r"Pianc[oó]", "CCCCCCCC"),
    (r"Sousa", "CCCCCCCC"),
    (r"diesp@tjpb\.jus\.br", "ppppp@ppppp.ppp"),
]

SENSITIVE_NAME_TOKENS = re.compile(
    r"(?i)\b(christine|claudia|mara|francisca|robson|canan[eé]a|mamanguape|pianc[oó]|studart|socorro)\b"
)


def _run_pdftotext_layout(pdf_path: Path) -> str:
    raw = subprocess.check_output(
        ["pdftotext", "-layout", str(pdf_path), "-"], cwd=REPO_ROOT
    )
    return raw.decode("utf-8", errors="ignore")


def _replace_digits_keep_shape(text: str) -> str:
    return re.sub(r"\d", "0", text)


def sanitize_line(raw_line: str) -> str:
    line = raw_line.rstrip("\n")
    stripped = line.strip()
    if not stripped:
        return ""

    s = line

    for pat, rep in REPLACEMENTS:
        s = re.sub(pat, rep, s, flags=re.IGNORECASE)

    # Canonical legal lines to stable typed patterns.
    if re.search(r"(?i)^\s*interessad[oa]:", s):
        return "Interessada: PPPPP PPPPP PPPPP PPPPP - Perita Médica Neurologista"

    if re.search(r"(?i)\bmovido por\b", s):
        s = re.sub(r"(?i)^.*\bmovido por\b.*$", "movido por AAAAA AAAAAAA AAAAA AA", s)

    if re.search(r"(?i)\bem face\b", s):
        s = re.sub(
            r"(?i)^.*\bem face\b.*$",
            "em face de BBBBB BBBBBBB BBBBB BB",
            s,
        )

    if re.search(r"(?i)^\s*requerente:", s):
        return "Requerente: Juízo da 1ª Vara da Comarca de CCCCCCCC"

    if re.search(r"(?i)\bperante\b.*\bju[ií]zo\b", s):
        return "perante o Juízo da 0ª Vara da Comarca de CCCCCCCC."

    if re.search(r"(?i)\bjo[aã]o pessoa\b.*\bde\b.*\bde\b", s):
        return "João Pessoa, 00 de abril de 2024."

    # Numbers and ids.
    s = re.sub(r"\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b", "0000000-00.0000.0.00.0000", s)
    s = re.sub(r"Processo\s*n[ºo]\s*\d{4}\.\d{3}\.\d{3}", "Processo nº 2022.000.000", s, flags=re.IGNORECASE)
    s = re.sub(r"R\$\s*\d+[\.,]?\s*\d*", "R$ 000,00", s)
    s = re.sub(r"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b", "000.000.000-00", s)
    s = re.sub(r"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b", "00.000.000/0000-00", s)
    s = re.sub(r"\b\d{11}\b", "00000000000", s)
    s = re.sub(r"\b\d{1,2}/\d{1,2}/\d{2,4}\b", "00/00/0000", s)

    # Generic name token cleanup fallback.
    if SENSITIVE_NAME_TOKENS.search(s):
        s = SENSITIVE_NAME_TOKENS.sub("PPPPP", s)

    # Keep legal style readable while sanitizing numeric leaks.
    if "Vara" not in s:
        s = _replace_digits_keep_shape(s)
    s = s.replace("Processo nº 0000.000.000", "Processo nº 2022.000.000")
    s = s.replace("00 de abril de 0000", "00 de abril de 2024")

    return re.sub(r"\s{2,}", " ", s).strip()


def render_pages_to_pdf(pages: list[str], out_path: Path) -> None:
    out_path.parent.mkdir(parents=True, exist_ok=True)
    c = canvas.Canvas(str(out_path), pagesize=A4)
    width, height = A4
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


def main() -> None:
    for src, dst in SOURCE_TARGETS:
        text = _run_pdftotext_layout(src)
        pages = text.split("\f")
        if pages and not pages[-1].strip():
            pages = pages[:-1]
        render_pages_to_pdf(pages, dst)
        print(f"generated: {dst}")


if __name__ == "__main__":
    main()
