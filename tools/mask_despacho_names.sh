#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

if [[ -f ".env" ]]; then
  # shellcheck disable=SC1091
  source .env
fi

SOURCE_DIR="${1:-${OBJPDF_ALIAS_D_DIR:-/mnt/c/git/operpdf/reference/models/despachos}}"
LIMIT="${2:-10}"
OUT_DIR="${3:-outputs/anonymized_despachos}"
MODEL_INPUT="${4:-@MODEL}"

if [[ ! -d "$SOURCE_DIR" ]]; then
  echo "Erro: diretório de despachos não encontrado: $SOURCE_DIR" >&2
  exit 2
fi

mkdir -p "$OUT_DIR"

mapfile -t PDFS < <(find "$SOURCE_DIR" -type f -iname '*.pdf' | sort | head -n "$LIMIT")
if [[ ${#PDFS[@]} -eq 0 ]]; then
  echo "Nenhum PDF encontrado em: $SOURCE_DIR" >&2
  exit 2
fi

echo "[MASK_NAMES] source=$SOURCE_DIR limit=${#PDFS[@]} out=$OUT_DIR model=$MODEL_INPUT"

ok_count=0
fail_count=0

for pdf in "${PDFS[@]}"; do
  base="$(basename "$pdf")"
  stem="${base%.*}"
  raw_name="maskraw__${stem}.json"
  raw_path="io/${raw_name}"
  masked_path="${OUT_DIR}/${stem}__masked.json"
  run_log="/tmp/mask_despacho_${stem}.log"

  if ./align.exe textopsalign-despacho --inputs "$MODEL_INPUT" --inputs "$pdf" --return "$raw_name" >"$run_log" 2>&1; then
    if [[ ! -f "$raw_path" ]]; then
      echo "  FAIL raw ausente: $base" >&2
      fail_count=$((fail_count + 1))
      continue
    fi

    jq '
      def mask_name:
        if type == "string" then
          gsub("[A-Za-zÀ-ÖØ-öø-ÿ]"; "P")
        else
          .
        end;

      def mask_values:
        if type == "object" then
          (if has("PERITO") then .PERITO |= mask_name else . end
          | if has("PROMOVENTE") then .PROMOVENTE |= mask_name else . end
          | if has("PROMOVIDO") then .PROMOVIDO |= mask_name else . end)
        else
          .
        end;

      def mask_fields:
        if type == "object" then
          with_entries(
            if (.key == "PERITO" or .key == "PROMOVENTE" or .key == "PROMOVIDO") and (.value|type == "object") then
              .value |=
                (if has("Value") then .Value |= mask_name else . end
                | if has("ValueRaw") then .ValueRaw |= mask_name else . end)
            else
              .
            end
          )
        else
          .
        end;

      if (.Extraction|type == "object") and (.Extraction.parsed|type == "object") then
        .Extraction.parsed |=
          (if has("pdf_a") and (.pdf_a|type == "object") then
             .pdf_a |=
               (if has("values") then .values |= mask_values else . end
               | if has("fields") then .fields |= mask_fields else . end)
           else . end
          | if has("pdf_b") and (.pdf_b|type == "object") then
             .pdf_b |=
               (if has("values") then .values |= mask_values else . end
               | if has("fields") then .fields |= mask_fields else . end)
            else . end)
      else
        .
      end
    ' "$raw_path" > "$masked_path"

    echo "  OK  $base -> $masked_path"
    ok_count=$((ok_count + 1))
  else
    echo "  FAIL $base (veja $run_log)" >&2
    fail_count=$((fail_count + 1))
  fi
done

echo "[MASK_NAMES] done ok=$ok_count fail=$fail_count out=$OUT_DIR"
if [[ $fail_count -gt 0 ]]; then
  exit 1
fi

