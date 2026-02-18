# operpdf

Rota oficial de CLI: `operpdf` (projeto `cli/OperCli/OperCli.csproj`).

Alias legado de CLI foi removido da superfície principal para evitar rota duplicada.

Documentação antiga foi arquivada em `docs/LEGADO.md`.

## Comandos principais
- Detectar/extrair despacho:
  `operpdf inspect despacho --input :Q1`
- Pattern match despacho:
  `operpdf pattern match --patterns despacho --inputs :Q1-20`
- Pattern match certidão CM:
  `operpdf pattern match --patterns tjpb_certidao_cm --inputs :Q1-20`
- Pattern match requerimento:
  `operpdf pattern match --patterns tjpb_requerimento --inputs :Q1-20`
- Build:
  `operpdf build`

## Modo Dev (selecionar PDF por índice)
Para testar sem digitar caminho completo, use aliases `:Qn`:

```
operpdf inspect list --input :Q12
```

Dois arquivos (alinhamento):

```
operpdf textopsalign :Q12 :Q15
```

Por padrão, o diretório vem de `OBJPDF_DEV_DIR` (ou `OBJ_FIND_INPUT_DIR`).

## Configs (root repo)
- Rules: `modules/PatternModules/registry/textops_rules/`
- Anchors: `modules/PatternModules/registry/textops_anchors/`
- Field maps: `modules/PatternModules/registry/extract_fields/`
- Alignrange maps: `modules/PatternModules/registry/alignrange_fields/`
- Defaults (auto-loaded): `modules/PatternModules/registry/models/obj_defaults.yml`

## Extraction core (now inside OBJ)
The full `Obj.TjpbDespachoExtractor` pipeline now lives here:
- Commands: `modules/ExtractionModule/TjpbDespachoExtractor/Commands/`
- Extraction engine: `modules/ExtractionModule/TjpbDespachoExtractor/Extraction/`
- Models/Config/Reference/Utils: `modules/ExtractionModule/TjpbDespachoExtractor/*`

## Encapsulated modules (no CLI coupling)
- Align engine: `modules/Align/ObjectsTextOpsDiff.cs` (diff/align logic, alignrange core, ROI helpers).
- Document detector: `modules/DocDetector/` (bookmark and /Contents-based title detection).
- Front/back resolver: `modules/FrontBack/` (stream selection + alignrange in one call).
- Camins detector: `tools/camins_detector/` (TF‑IDF / k-means / simhash helpers for subtype clustering).

## Modules (encapsulated)
- Align: `modules/Align/ObjectsTextOpsDiff.cs`
- TextOpsRanges: `modules/TextOpsRanges/` (fixos/variaveis + alignrange helpers)
- DocDetector: `modules/DocDetector/`
- CaminsDetector: `tools/camins_detector/`
