You are the new Codex in /mnt/c/git/tjpdf/OBJ.

GOAL (strict):
- Process the folder `/mnt/c/Users/pichau/Desktop/geral_pdf/quarentena` and finish with ALL PDFs having fields extracted with:
  - no errors
  - no false positives
- JSON must contain for each field: Value, ValueFull, ValueRaw, OpRange, Obj, BBox.

TIME-BOXED STEPS (short runs only):
1) Inspect AlignRange outputs (code + one real run). 10 min max.
2) Inspect MapFields outputs (code + one real run). 10 min max.
3) Identify the minimal code change needed so despacho/certidão/requerimento all yield full JSON. 10 min max.
4) Apply only that minimal change. 10 min max.
5) Run the pipeline on the quarentena directory and produce a summary report.

MODELS (PDFs):
Use these as model PDFs:
- /mnt/c/git/tjpdf/OBJ/reference/models/tjpb_despacho_model.pdf
- /mnt/c/git/tjpdf/OBJ/reference/models/tjpb_requerimento_model.pdf
- /mnt/c/git/tjpdf/OBJ/reference/models/tjpb_certidao_conselho_model.pdf
(models listed in /mnt/c/git/tjpdf/OBJ/modules/PatternModules/registry/models/obj_models.yml)

IMPORTANT:
- Do NOT edit flowcharts.
- Do NOT run long batch jobs before confirming a minimal fix works.
- If any example PDF is missing, use the model PDF and report it.
