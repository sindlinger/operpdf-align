#!/usr/bin/env python3
from __future__ import annotations

import argparse
import concurrent.futures
import datetime as dt
import json
import os
import re
import statistics
import subprocess
import sys
from pathlib import Path
from typing import Any

ANSI_RE = re.compile(r"\x1b\[[0-9;]*m")
PROBE_RE = re.compile(r"\[PROBE\].*?found=(\d+)/(\d+)\s+missing=(\d+)")
TOTAL_RE = re.compile(r"Total arquivos:\s*(\d+)")
FILE_RE = re.compile(r"\[PROBE\].*?file=([^\s]+)")


def strip_ansi(s: str) -> str:
    return ANSI_RE.sub("", s)


def run_cmd(cmd: list[str], cwd: Path, timeout_sec: int) -> tuple[int, str]:
    try:
        p = subprocess.run(
            cmd,
            cwd=str(cwd),
            text=True,
            capture_output=True,
            timeout=timeout_sec,
            check=False,
        )
        out = (p.stdout or "") + "\n" + (p.stderr or "")
        return p.returncode, strip_ansi(out)
    except subprocess.TimeoutExpired as ex:
        out = (ex.stdout or "") + "\n" + (ex.stderr or "")
        return 124, strip_ansi(out)


def build_runner_cmd(repo: Path, mode: str, runner_path: str) -> list[str]:
    if mode == "dll":
        return ["dotnet", "cli/OperCli/bin/Release/net8.0/operpdf.dll"]
    if mode == "exe":
        path = runner_path.strip() or "./align.exe"
        return [path]
    raise ValueError(f"runner invalido: {mode}")


def discover_total(base_cmd: list[str], repo: Path, alias: str, timeout_sec: int) -> int:
    cmd = [
        *base_cmd,
        "textopsalign-despacho",
        "--inputs",
        "@M-DESP",
        "--inputs",
        f":{alias}999999",
        "--probe",
        "--sem-alinhamento",
    ]
    code, out = run_cmd(cmd, repo, timeout_sec)
    _ = code
    m = TOTAL_RE.search(out)
    if not m:
        raise RuntimeError(f"Nao consegui descobrir total de :{alias}. Saida:\n{out[:800]}")
    return int(m.group(1))


def task(
    base_cmd: list[str], repo: Path, alias: str, idx: int, timeout_sec: int, with_objdiff: bool
) -> dict[str, Any]:
    cmd = [
        *base_cmd,
        "textopsrun-despacho",
        "run",
        "1-8",
        "--inputs",
        "@M-DESP",
        "--inputs",
        f":{alias}{idx}",
        "--probe",
        "--sem-alinhamento",
    ]
    if with_objdiff:
        cmd.append("--with-objdiff")

    code, out = run_cmd(cmd, repo, timeout_sec)
    probe_matches = list(PROBE_RE.finditer(out))
    first_probe = probe_matches[0] if probe_matches else None

    found = int(first_probe.group(1)) if first_probe else 0
    checked = int(first_probe.group(2)) if first_probe else 0
    missing = int(first_probe.group(3)) if first_probe else 0

    file_match = FILE_RE.search(out)
    file_name = file_match.group(1) if file_match else ""

    ratio = (found / checked) if checked > 0 else None

    return {
        "alias": alias,
        "index": idx,
        "exit_code": code,
        "file": file_name,
        "probe_found": found,
        "probe_checked": checked,
        "probe_missing": missing,
        "probe_ratio": ratio,
        "probe_ok": (checked > 0 and ratio is not None and ratio >= 0.95),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Benchmark de acuracia (probe) para :D e :Q")
    parser.add_argument("--repo", default=".")
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--timeout", type=int, default=180)
    parser.add_argument("--max-d", type=int, default=0, help="Limita quantidade de indices D (0=todos)")
    parser.add_argument("--max-q", type=int, default=0, help="Limita quantidade de indices Q (0=todos)")
    parser.add_argument("--with-objdiff", action="store_true")
    parser.add_argument(
        "--runner",
        choices=["dll", "exe"],
        default="dll",
        help="Modo de execucao: dll (dotnet operpdf.dll) ou exe (align.exe).",
    )
    parser.add_argument(
        "--runner-path",
        default="./align.exe",
        help="Caminho do executavel quando --runner exe.",
    )
    args = parser.parse_args()

    repo = Path(args.repo).resolve()
    if args.runner == "dll" and not (repo / "cli/OperCli/bin/Release/net8.0/operpdf.dll").exists():
        print("operpdf.dll nao encontrado. Rode build primeiro.", file=sys.stderr)
        return 2
    if args.runner == "exe" and not (repo / args.runner_path).exists():
        print(f"Executavel nao encontrado: {repo / args.runner_path}", file=sys.stderr)
        return 2

    base_cmd = build_runner_cmd(repo, args.runner, args.runner_path)

    total_d = discover_total(base_cmd, repo, "D", args.timeout)
    total_q = discover_total(base_cmd, repo, "Q", args.timeout)

    if args.max_d > 0:
        total_d = min(total_d, args.max_d)
    if args.max_q > 0:
        total_q = min(total_q, args.max_q)

    tasks: list[tuple[str, int]] = []
    tasks.extend(("D", i) for i in range(1, total_d + 1))
    tasks.extend(("Q", i) for i in range(1, total_q + 1))

    print(f"[BENCH] total D={total_d} Q={total_q} total_tasks={len(tasks)} workers={args.workers}")

    rows: list[dict[str, Any]] = []
    done = 0
    ok = 0

    with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
        futs = [
            ex.submit(task, base_cmd, repo, alias, idx, args.timeout, args.with_objdiff)
            for alias, idx in tasks
        ]
        for fut in concurrent.futures.as_completed(futs):
            row = fut.result()
            rows.append(row)
            done += 1
            if row["exit_code"] == 0:
                ok += 1
            if done % 20 == 0 or done == len(tasks):
                print(f"[BENCH] progresso {done}/{len(tasks)} exit_ok={ok}")

    rows.sort(key=lambda r: (r["alias"], r["index"]))

    checked_rows = [r for r in rows if isinstance(r.get("probe_ratio"), float)]
    weighted_found = sum(int(r["probe_found"]) for r in checked_rows)
    weighted_checked = sum(int(r["probe_checked"]) for r in checked_rows)
    weighted_ratio = (weighted_found / weighted_checked) if weighted_checked > 0 else 0.0

    ratios = [float(r["probe_ratio"]) for r in checked_rows]
    ratio_mean = statistics.mean(ratios) if ratios else 0.0

    ok95 = sum(1 for r in checked_rows if float(r["probe_ratio"]) >= 0.95)

    summary = {
        "generated_at_utc": dt.datetime.utcnow().isoformat() + "Z",
        "workers": args.workers,
        "timeout_sec": args.timeout,
        "runner": args.runner,
        "runner_path": args.runner_path if args.runner == "exe" else "",
        "with_objdiff": bool(args.with_objdiff),
        "totals": {
            "D": total_d,
            "Q": total_q,
            "tasks": len(tasks),
            "exit_ok": ok,
            "exit_fail": len(tasks) - ok,
        },
        "probe": {
            "weighted_found": weighted_found,
            "weighted_checked": weighted_checked,
            "weighted_ratio": weighted_ratio,
            "mean_ratio": ratio_mean,
            "files_with_ratio": len(checked_rows),
            "files_ratio_ge_95": ok95,
            "files_ratio_lt_95": len(checked_rows) - ok95,
        },
        "rows": rows,
    }

    out_dir = repo / "run" / "io"
    out_dir.mkdir(parents=True, exist_ok=True)
    out_file = out_dir / f"bench_dq_accuracy_{dt.datetime.utcnow().strftime('%Y%m%dT%H%M%SZ')}.json"
    out_file.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"[BENCH] relatório: {out_file}")
    print(f"[BENCH] weighted_ratio={weighted_ratio:.4f} mean_ratio={ratio_mean:.4f} >=95%={ok95}/{len(checked_rows)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
