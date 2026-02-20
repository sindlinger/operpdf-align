#!/usr/bin/env python3
import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Dict, List, Optional, Tuple


ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")
DEFAULT_MODES = [
    "textopsalign-despacho",
    "textopsvar-despacho",
    "textopsfixed-despacho",
]


@dataclass
class RunMetrics:
    pairs: Optional[int] = None
    fixed: Optional[int] = None
    variable: Optional[int] = None
    gaps: Optional[int] = None
    helper_used: Optional[int] = None
    range_a: Optional[str] = None
    range_b: Optional[str] = None
    validator_ok: Optional[bool] = None
    validator_reason: Optional[str] = None
    probe_found: Optional[int] = None
    probe_total: Optional[int] = None
    probe_missing: Optional[int] = None


@dataclass
class RunResult:
    model_label: str
    model_path: str
    mode: str
    exit_code: int
    log_path: str
    metrics: RunMetrics


def strip_ansi(text: str) -> str:
    return ANSI_RE.sub("", text or "")


def parse_int(text: str, pattern: str) -> Optional[int]:
    match = re.search(pattern, text, re.MULTILINE)
    if not match:
        return None
    try:
        return int(match.group(1))
    except ValueError:
        return None


def parse_str(text: str, pattern: str) -> Optional[str]:
    match = re.search(pattern, text, re.MULTILINE)
    if not match:
        return None
    value = match.group(1).strip()
    return value if value else None


def parse_metrics(raw_output: str) -> RunMetrics:
    text = strip_ansi(raw_output)
    metrics = RunMetrics()
    metrics.pairs = parse_int(text, r"^\s*pairs:\s*(\d+)\s*$")
    metrics.fixed = parse_int(text, r"^\s*fixed:\s*(\d+)\s*$")
    metrics.variable = parse_int(text, r"^\s*variable:\s*(\d+)\s*$")
    metrics.gaps = parse_int(text, r"^\s*gaps:\s*(\d+)\s*$")
    metrics.helper_used = parse_int(text, r"^\s*helperUsed:\s*(\d+)\s*$")
    if metrics.helper_used is None:
        metrics.helper_used = parse_int(text, r"^\s*helper_used:\s*(\d+)\s*$")
    metrics.range_a = parse_str(text, r"^\s*rangeA:\s*(.+?)\s*$")
    metrics.range_b = parse_str(text, r"^\s*rangeB:\s*(.+?)\s*$")

    validator = re.search(r'^\[VALIDATOR\]\s+(true|false)(?:\s+reason="([^"]*)")?', text, re.MULTILINE)
    if validator:
        metrics.validator_ok = validator.group(1).lower() == "true"
        metrics.validator_reason = (validator.group(2) or "").strip() or None

    probe = re.search(r"^\[PROBE\]\s+\w+.*found=(\d+)/(\d+)\s+missing=(\d+)", text, re.MULTILINE)
    if probe:
        metrics.probe_found = int(probe.group(1))
        metrics.probe_total = int(probe.group(2))
        metrics.probe_missing = int(probe.group(3))

    return metrics


def safe_name(raw: str) -> str:
    return re.sub(r"[^a-zA-Z0-9_.-]+", "_", raw)


def run_case(
    binary: str,
    mode: str,
    model_path: str,
    target_input: str,
    model_label: str,
    work_dir: Path,
    extra_args: List[str],
) -> RunResult:
    cmd = [binary, mode, "--inputs", model_path, "--inputs", target_input, "--probe"]
    cmd.extend(extra_args)
    proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    output = proc.stdout or ""

    log_name = f"{safe_name(mode)}__{model_label}.log"
    log_path = work_dir / log_name
    log_path.write_text(output, encoding="utf-8")

    return RunResult(
        model_label=model_label,
        model_path=model_path,
        mode=mode,
        exit_code=proc.returncode,
        log_path=str(log_path),
        metrics=parse_metrics(output),
    )


def format_int(value: Optional[int]) -> str:
    return "-" if value is None else str(value)


def format_bool(value: Optional[bool]) -> str:
    if value is None:
        return "-"
    return "true" if value else "false"


def print_results(results: List[RunResult]) -> None:
    header = [
        "mode",
        "model",
        "exit",
        "pairs",
        "fixed",
        "variable",
        "gaps",
        "helper",
        "probe",
        "validator",
        "reason",
    ]
    rows: List[List[str]] = []
    for result in results:
        probe = "-"
        if result.metrics.probe_found is not None and result.metrics.probe_total is not None and result.metrics.probe_missing is not None:
            probe = f"{result.metrics.probe_found}/{result.metrics.probe_total} miss={result.metrics.probe_missing}"
        rows.append(
            [
                result.mode,
                result.model_label,
                str(result.exit_code),
                format_int(result.metrics.pairs),
                format_int(result.metrics.fixed),
                format_int(result.metrics.variable),
                format_int(result.metrics.gaps),
                format_int(result.metrics.helper_used),
                probe,
                format_bool(result.metrics.validator_ok),
                result.metrics.validator_reason or "-",
            ]
        )

    widths = [len(col) for col in header]
    for row in rows:
        for i, col in enumerate(row):
            if len(col) > widths[i]:
                widths[i] = len(col)

    def print_row(cols: List[str]) -> None:
        out = "  ".join(col.ljust(widths[i]) for i, col in enumerate(cols))
        print(out)

    print_row(header)
    print_row(["-" * w for w in widths])
    for row in rows:
        print_row(row)


def build_mode_map(results: List[RunResult]) -> Dict[Tuple[str, str], RunResult]:
    return {(r.mode, r.model_label): r for r in results}


def evaluate_gate(
    results: List[RunResult],
    max_probe_missing: Optional[int],
    min_probe_found: Optional[int],
    max_gap_delta: Optional[int],
    max_variable_drop: Optional[int],
) -> List[str]:
    failures: List[str] = []

    for result in results:
        if result.exit_code != 0:
            failures.append(f"{result.mode}/{result.model_label}: exit={result.exit_code}")
        if min_probe_found is not None:
            found = result.metrics.probe_found
            if found is None or found < min_probe_found:
                failures.append(f"{result.mode}/{result.model_label}: probe_found={found} < {min_probe_found}")
        if max_probe_missing is not None:
            missing = result.metrics.probe_missing
            if missing is None or missing > max_probe_missing:
                failures.append(f"{result.mode}/{result.model_label}: probe_missing={missing} > {max_probe_missing}")

    if max_gap_delta is not None or max_variable_drop is not None:
        by_mode = build_mode_map(results)
        for mode in sorted({r.mode for r in results}):
            a = by_mode.get((mode, "A"))
            b = by_mode.get((mode, "B"))
            if not a or not b:
                continue

            if max_gap_delta is not None and a.metrics.gaps is not None and b.metrics.gaps is not None:
                delta = b.metrics.gaps - a.metrics.gaps
                if delta > max_gap_delta:
                    failures.append(f"{mode}: gap_delta(B-A)={delta} > {max_gap_delta}")

            if max_variable_drop is not None and a.metrics.variable is not None and b.metrics.variable is not None:
                drop = a.metrics.variable - b.metrics.variable
                if drop > max_variable_drop:
                    failures.append(f"{mode}: variable_drop(A-B)={drop} > {max_variable_drop}")

    return failures


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compara dois modelos de despacho (A/B) nos modos align/var/fixed e aplica gate de regressao.",
    )
    parser.add_argument("--bin", default="./cli/OperCli/bin/Release/net8.0/operpdf", help="Caminho do binario operpdf.")
    parser.add_argument("--model-a", required=True, help="PDF modelo A (baseline).")
    parser.add_argument("--model-b", required=True, help="PDF modelo B.")
    parser.add_argument("--target", default=":Q22", help="Input alvo para comparar com os modelos.")
    parser.add_argument(
        "--modes",
        default=",".join(DEFAULT_MODES),
        help="Lista separada por virgula dos modos a executar.",
    )
    parser.add_argument("--work-dir", default="/tmp/operpdf_ab_compare", help="Diretorio dos logs.")
    parser.add_argument("--json-out", default="", help="Arquivo JSON opcional para salvar resultados.")
    parser.add_argument("--min-probe-found", type=int, default=None, help="Gate opcional: minimo de campos found no PROBE.")
    parser.add_argument("--max-probe-missing", type=int, default=None, help="Gate opcional: maximo de campos missing no PROBE.")
    parser.add_argument("--max-gap-delta", type=int, default=None, help="Gate opcional: maximo de aumento de gaps de A->B por modo.")
    parser.add_argument("--max-variable-drop", type=int, default=None, help="Gate opcional: maximo de queda de variable de A->B por modo.")
    parser.add_argument("--extra-arg", action="append", default=[], help="Argumento extra repassado para cada comando.")
    parser.add_argument("--no-fail-on-gate", action="store_true", help="Nao retorna codigo !=0 quando o gate falhar.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    binary = os.path.abspath(args.bin)
    if not os.path.exists(binary):
        print(f"erro: binario nao encontrado: {binary}", file=sys.stderr)
        return 2

    model_a = os.path.abspath(args.model_a)
    model_b = os.path.abspath(args.model_b)
    for model in (model_a, model_b):
        if not os.path.exists(model):
            print(f"erro: modelo nao encontrado: {model}", file=sys.stderr)
            return 2

    modes = [v.strip() for v in (args.modes or "").split(",") if v.strip()]
    if not modes:
        print("erro: lista de modos vazia", file=sys.stderr)
        return 2

    work_dir = Path(args.work_dir)
    work_dir.mkdir(parents=True, exist_ok=True)

    results: List[RunResult] = []
    for mode in modes:
        results.append(
            run_case(
                binary=binary,
                mode=mode,
                model_path=model_a,
                target_input=args.target,
                model_label="A",
                work_dir=work_dir,
                extra_args=args.extra_arg,
            )
        )
        results.append(
            run_case(
                binary=binary,
                mode=mode,
                model_path=model_b,
                target_input=args.target,
                model_label="B",
                work_dir=work_dir,
                extra_args=args.extra_arg,
            )
        )

    print("AB COMPARATOR (DESPACHO)")
    print(f"  bin: {binary}")
    print(f"  model A: {model_a}")
    print(f"  model B: {model_b}")
    print(f"  target: {args.target}")
    print(f"  logs: {work_dir}")
    print()
    print_results(results)
    print()

    failures = evaluate_gate(
        results=results,
        max_probe_missing=args.max_probe_missing,
        min_probe_found=args.min_probe_found,
        max_gap_delta=args.max_gap_delta,
        max_variable_drop=args.max_variable_drop,
    )

    payload = {
        "bin": binary,
        "model_a": model_a,
        "model_b": model_b,
        "target": args.target,
        "modes": modes,
        "results": [asdict(r) for r in results],
        "gate": {
            "min_probe_found": args.min_probe_found,
            "max_probe_missing": args.max_probe_missing,
            "max_gap_delta": args.max_gap_delta,
            "max_variable_drop": args.max_variable_drop,
            "failures": failures,
            "ok": len(failures) == 0,
        },
    }
    if args.json_out:
        out_path = Path(args.json_out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"json salvo: {out_path}")

    if failures:
        print("GATE: FAIL")
        for failure in failures:
            print(f"  - {failure}")
        return 0 if args.no_fail_on_gate else 3

    print("GATE: PASS")
    return 0


if __name__ == "__main__":
    sys.exit(main())
