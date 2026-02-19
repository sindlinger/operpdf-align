# Validator Centralization Map

## Central Modules (source of truth)

- `modules/ValidatorModule/ValidatorRules.cs`
  - Field-level normalization, cleaning and validation rules.
  - Used by `ObjectsPattern`, `FieldExtractor`, `FieldStrategyEngine`, `HonorariosEnricher`, and related extraction flows.

- `modules/ValidatorModule/DocumentValidationRules.cs`
  - Document detection validation rules (guard/strong/weak/block/signature metadata checks).
  - Public checks:
    - `IsCertidaoConselho(...)`
    - `IsCertidaoConselhoFromTopBody(...)`
    - `IsRequerimento(...)`
    - `IsRequerimentoStrong(...)`
    - `IsRequerimentoFromTopBody(...)`
    - `IsDespacho(...)`
    - `IsBlockedDespacho(...)`
    - `MatchGuard(...)`
    - `ContainsAnyGroup(...)`
    - `HasStrongSignals(...)`
    - `LooksLikeSignatureMetadataPage(...)`

## Migrated from scattered code

- `modules/DocDetector/Detectors/NonDespachoDetector.cs`
  - Removed local duplicate implementations for guard/group/strong/signature logic.
  - Now consumes `DocumentValidationRules`.

- `src/Commands/Inspect/ObjectsPipeline.cs`
  - Removed local duplicate implementations for guard/group/requerimento/certidão/despacho checks.
  - Now consumes `DocumentValidationRules`.

## Still scattered (pending migration to central module)

- `src/Commands/Inspect/ObjectsPattern.cs`
  - Contains flow-specific validator orchestration and wrappers around `ValidatorRules`.
  - Candidate for extraction into a dedicated validator orchestrator service.

- `modules/ExtractionModule/TjpbDespachoExtractor/Extraction/FieldExtractor.cs`
  - Post-validate and extraction-stage validation decisions.
  - Calls `ValidatorRules`, but still owns branch-heavy local policy.

- `modules/ExtractionModule/TjpbDespachoExtractor/Extraction/FieldStrategyEngine.cs`
  - Strategy-time validation gates (`validate` lists) and fallback policy.

- `modules/AnchorTemplateExtractor/FieldValidators.cs`
  - Template extractor has a parallel local validator set.
  - Candidate for unification with `ValidatorRules` where semantics match.

## Goal for full zero-scatter

- Keep only:
  - `ValidatorRules` for field semantics.
  - `DocumentValidationRules` for doc-type semantics.
- Convert command/extractor layers to orchestration-only (no duplicated validation logic).

## Official field sets (current registry)

- `tjpb_requerimento`
  - Pattern file: `modules/PatternModules/registry/patterns/tjpb_requerimento.json`
  - Core extracted fields: `PROCESSO_ADMINISTRATIVO`, `PROCESSO_JUDICIAL`, `DATA_REQUISICAO`
  - This doc type does **not** have 20 extraction fields in the active pattern registry.

- `tjpb_certidao_cm`
  - Pattern file: `modules/PatternModules/registry/patterns/tjpb_certidao_cm.json`
  - Core extracted fields: `PROCESSO_ADMINISTRATIVO`, `PERITO`, `VALOR_ARBITRADO_CM`, `ADIANTAMENTO`, `PERCENTUAL`, `DATA_AUTORIZACAO_CM`
  - Additional regex templates exist in:
    - `modules/PatternModules/registry/template_fields/tjpb_certidao.yml`
  - This doc type also does **not** have 20 mandatory extraction fields in the active pattern registry.
