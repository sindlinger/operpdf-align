#!/usr/bin/env python3
"""
validate_output.py

Validate TJPDF output JSON against references/output_schema.json.

Usage:
  python scripts/validate_output.py --json path/to/output.json
  python scripts/validate_output.py --json path/to/output.json --schema references/output_schema.json

Notes:
  - Uses `jsonschema` if installed.
  - Falls back to a lightweight validator if `jsonschema` is not available.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any, Dict, List, Tuple


FINAL_FIELDS = [
    "PROCESSO_ADMINISTRATIVO",
    "PROCESSO_JUDICIAL",
    "COMARCA",
    "VARA",
    "PROMOVENTE",
    "PROMOVIDO",
    "PERITO",
    "CPF_PERITO",
    "ESPECIALIDADE",
    "ESPECIE_DA_PERICIA",
    "VALOR_ARBITRADO_JZ",
    "VALOR_ARBITRADO_DE",
    "VALOR_ARBITRADO_CM",
    "VALOR_ARBITRADO_FINAL",
    "DATA_ARBITRADO_FINAL",
    "DATA_REQUISICAO",
    "ADIANTAMENTO",
    "PERCENTUAL",
    "PARCELA",
    "FATOR",
]


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8"))


def try_jsonschema_validate(instance: Any, schema: Dict[str, Any]) -> Tuple[bool, str]:
    try:
        import jsonschema  # type: ignore
        jsonschema.validate(instance=instance, schema=schema)
        return True, "jsonschema: ok"
    except ImportError:
        return False, "jsonschema not installed"
    except Exception as e:
        return False, f"jsonschema validation failed: {e}"


def lightweight_validate(data: Any) -> List[str]:
    errors: List[str] = []
    if not isinstance(data, dict):
        return ["Root must be an object/dict"]

    if "meta" not in data or "documents" not in data:
        errors.append("Missing required keys: meta, documents")

    docs = data.get("documents")
    if not isinstance(docs, list) or len(docs) == 0:
        errors.append("documents must be a non-empty array")

    for i, d in enumerate(docs if isinstance(docs, list) else []):
        if not isinstance(d, dict):
            errors.append(f"documents[{i}] must be an object")
            continue

        for k in ["doc_type", "pages", "confidence", "final_fields", "field_results"]:
            if k not in d:
                errors.append(f"documents[{i}] missing required key: {k}")

        ff = d.get("final_fields")
        if isinstance(ff, dict):
            missing = [k for k in FINAL_FIELDS if k not in ff]
            if missing:
                errors.append(f"documents[{i}].final_fields missing keys: {missing}")
        else:
            errors.append(f"documents[{i}].final_fields must be an object")

    return errors


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--json", required=True, help="Path to output JSON")
    ap.add_argument("--schema", default=str(Path(__file__).resolve().parent.parent / "references" / "output_schema.json"))
    args = ap.parse_args()

    json_path = Path(args.json).resolve()
    schema_path = Path(args.schema).resolve()

    if not json_path.exists():
        print(f"ERROR: JSON not found: {json_path}")
        return 2
    if not schema_path.exists():
        print(f"ERROR: schema not found: {schema_path}")
        return 2

    data = load_json(json_path)
    schema = load_json(schema_path)

    ok, msg = try_jsonschema_validate(data, schema)
    if ok:
        print("VALID ✅", msg)
        return 0

    # fallback
    errors = lightweight_validate(data)
    if errors:
        print("INVALID ❌")
        for e in errors:
            print("-", e)
        print("")
        print("Note:", msg)
        return 1

    print("VALID ✅ (lightweight)")
    print("Note:", msg)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
