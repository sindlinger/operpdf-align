You are the new Codex in this repo: /mnt/c/git/tjpdf/OBJ.

MISSION:
1) Review the current pipeline BEFORE any change.
2) Find AlignRange (line range by operators) and document:
   - where the code lives,
   - which inputs it receives,
   - which outputs it generates today (TXT/JSON/YAML),
   - which step generates the full JSON with op_range, value_full, bbox, etc.
3) Check if AlignRange is underused (only returns range, not fields).
4) Confirm how DiffMatchPatch enters (fixed/variable) and how it feeds fields.

EXPECTED RESULT (mandatory):
- Extract ALL fields into JSON, with no errors, for the three documents:
  1) despacho
  2) certidao do conselho
  3) requerimento de honorarios
- JSON must contain per field: Value, ValueFull, ValueRaw, OpRange, Obj, BBox.
- One JSON per document (or a consolidated JSON, depending on current pipeline).

EXAMPLE PDFs (use for validation):
- Certidao do conselho:
  /mnt/c/git/tjpdf/outputs/quarentena_pages_full/2021065378__p41-42.pdf
- Despacho:
  /mnt/c/git/tjpdf/outputs/signatures/despachos_suspeitos_pages/0028287__p1.pdf
- Requerimento:
  /mnt/c/Users/pichau/Downloads/SEI_009203_14.2025.8.15.pdf

ENTRY POINTS:
- Pipeline: src/Commands/Inspect/ObjectsPipeline.cs
- AlignRange: TextOpsRanges/ObjectsTextOpsDiff.AlignRange.cs
- Align debug: TextOpsRanges/ObjectsTextOpsDiff.AlignDebug.cs
- MapFields: src/Commands/Inspect/ObjectsMapFields.cs
- TemplateFields/BBox: TextOpsRanges/ObjectsTextOpsDiff.TemplateFields.cs
- modules/PatternModules/registry/extract_fields/BBox: src/Commands/Inspect/ObjectsTextOpsExtractFields.cs

WHAT TO FIND:
- AlignRangeSummary: FrontA/FrontB/BackA/BackB (Page, StartOp, EndOp, ValueFull)
- Full JSON with fields + BBox (Value, ValueFull, ValueRaw, OpRange, Obj, BBox)
  -> identify which step produces it (AlignRange vs MapFields vs ExtractFields)
- Runtime outputs (outputs/*):
  - outputs/align_ranges/* (TXT + textops_align JSON)
  - outputs/objects_pipeline/* (debug/modules/alignrange/output/summary.json)
  - outputs/fields/* or outputs/objects_pipeline/*/mapfields/*.json

RULES:
- Do not modify code or flowcharts without explicit approval.
- Record everything with paths and real output examples.

OUTPUT:
- Short report of current pipeline.
- Minimal correction to reach the EXPECTED RESULT above.

ADDITIONAL NOTES (models vs templates):
- The strings found by `rg "Sala de Sessões"` and similar are NOT PDFs.
  They are template/guard strings inside configs/config.yaml.
- The actual PDF models are in:
  /mnt/c/git/tjpdf/OBJ/modules/PatternModules/registry/models/obj_models.yml
    despacho -> reference/models/tjpb_despacho_model.pdf
    requerimento_honorarios -> reference/models/tjpb_requerimento_model.pdf
    certidao_conselho -> reference/models/tjpb_certidao_conselho_model.pdf

Use those PDFs as the model (pdfB) when running AlignRange/MapFields.

If any certidão PDF example is missing, use the model PDF for validation and
record it explicitly in the report.
