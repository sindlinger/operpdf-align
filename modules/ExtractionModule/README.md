# Extraction Module (encapsulated)

This module hosts the full **TjpbDespachoExtractor** pipeline, isolated from CLI commands.
It receives document segments (already detected by the OBJ pipeline) and returns structured fields.

## Input → Output
- Input: document text segments (front_head/back_tail + page ranges)
- Output: `ExtractionResult` with fields, logs, and document metadata

## Structure
- `TjpbDespachoExtractor/Commands/` — CLI-style wrappers
- `TjpbDespachoExtractor/Extraction/` — core extraction logic (fields, regions, validation)
- `TjpbDespachoExtractor/Config/` — configs + hints
- `TjpbDespachoExtractor/Models/` — DTOs
- `TjpbDespachoExtractor/Reference/` — catalogs/tables
- `TjpbDespachoExtractor/Utils/` — helpers

## Notes
- This module is **called after AlignRange** (never detects documents).
- Regex/NLP must run **only** on the op_range/ValueFull recorte.
