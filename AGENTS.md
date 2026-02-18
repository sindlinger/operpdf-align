# AGENTS – operpdf

## Main objective (do not stop until complete)
Guarantee **100%** field extraction for:
- **Despacho** (quarantine :Q)
- **Certidão CM**
- **Requerimento**

## Execution flow (must follow)
1) Confirm repo path
2) Identify + extract despacho (p1+p2)
3) Pattern match batches for each doc type
4) Honorarios + Validator on every extraction
5) Export CSV/XLSX
6) Snapshot code
7) Build `operpdf.exe`
8) Diary update

Always applying:
- **Honorarios module**
- **Validator**
And exporting:
- **CSV + XLSX** to `outputs/extract/`

Also generate:
- **Responsible code snapshot** in `export/code_snapshot/`

## Commands
- `./operpdf inspect despacho --input :Q1`
- `./operpdf pattern match --patterns despacho --inputs :Q1-20`
- `./operpdf pattern match --patterns tjpb_certidao_cm --inputs :Q1-20`
- `./operpdf pattern match --patterns tjpb_requerimento --inputs :Q1-20`

## Critical files
- `modules/PatternModules/registry/patterns/*.json`
- `modules/PatternModules/registry/template_fields/*.yml`
- `src/Commands/Inspect/ObjectsPattern.cs`
- `src/Commands/Inspect/ObjectsFindDespacho.cs`
- `modules/HonorariosModule/*`
- `modules/ExtractionModule/TjpbDespachoExtractor/Reference/HonorariosTable.cs`

## Export
- CSV and XLSX required
- 1 row per PDF

## Responsible code snapshot
- `export/code_snapshot/` with:
  - `src/Commands/Inspect/*`
  - `modules/HonorariosModule/*`
  - `modules/ExtractionModule/TjpbDespachoExtractor/Reference/*`
  - `modules/PatternModules/registry/patterns/*`
  - `modules/PatternModules/registry/template_fields/*`
  - `operpdf.exe`

## Golden rule
NEVER REDUCE COVERAGE. ONLY EXPAND AND TUNE.
