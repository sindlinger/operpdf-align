#!/usr/bin/env python3
"""
derive_fields.py

Compute derived TJPDF fields:
- VALOR_ARBITRADO_FINAL
- DATA_ARBITRADO_FINAL

Rules are in references/final_fields.md.

Usage:
  python scripts/derive_fields.py --in fields.json --out fields_out.json

Input:
  JSON object containing at least:
    VALOR_ARBITRADO_JZ, VALOR_ARBITRADO_DE, VALOR_ARBITRADO_CM,
    plus candidate dates (repo-specific naming). This script provides a
    reference implementation; adapt field names to your repo contracts.

Note:
  This script is primarily meant as a deterministic reference for unit tests
  and to prevent regressions when refactoring mapfields.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, Optional, Tuple


def pick_final(valor_cm, valor_de, valor_jz, data_cm, data_despacho, data_req) -> Tuple[Any, Any, str]:
    """
    Return (valor_final, data_final, rule_used).
    """
    if valor_cm not in (None, "", 0):
        return valor_cm, data_cm, "CM"
    if valor_de not in (None, "", 0):
        return valor_de, data_despacho, "DE"
    return valor_jz, (data_despacho or data_req), "JZ"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", required=True, help="Input JSON path (object with fields)")
    ap.add_argument("--out", dest="out", required=True, help="Output JSON path")
    ap.add_argument("--data-cm-key", default="DATA_DECISAO_CM", help="Key used for CM decision date")
    ap.add_argument("--data-despacho-key", default="DATA_DESPACHO", help="Key used for despacho date")
    ap.add_argument("--data-req-key", default="DATA_REQUISICAO", help="Key used for requerimento date")
    args = ap.parse_args()

    inp = Path(args.inp).resolve()
    out = Path(args.out).resolve()

    data: Dict[str, Any] = json.loads(inp.read_text(encoding="utf-8"))

    valor_cm = data.get("VALOR_ARBITRADO_CM")
    valor_de = data.get("VALOR_ARBITRADO_DE")
    valor_jz = data.get("VALOR_ARBITRADO_JZ")

    data_cm = data.get(args.data_cm_key) or data.get("DATA_ARBITRADO_CM")
    data_despacho = data.get(args.data_despacho_key)
    data_req = data.get(args.data_req_key)

    valor_final, data_final, rule = pick_final(valor_cm, valor_de, valor_jz, data_cm, data_despacho, data_req)

    data["VALOR_ARBITRADO_FINAL"] = valor_final
    data["DATA_ARBITRADO_FINAL"] = data_final
    data["_DERIVATION_RULE"] = rule

    out.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Wrote: {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
