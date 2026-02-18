# Project Catalog

Purpose: authoritative high-level map of active code paths in `operpdf`.

Last update: 2026-02-14

## Active Command Surface
- Main binary: `operpdf` (`cli/OperCli`)
- Object inspection route (official): `operpdf inspect ...`
- Pattern/extraction route (official): `operpdf pattern ...`

## Core Entry Points
- CLI dispatcher: `cli/OperCli/Program.cs`
- Inspect commands: `src/Commands/Inspect/*`
- Pattern orchestration: `src/Commands/Inspect/ObjectsPattern.cs`

## Centralized Modules
- Core helpers: `modules/Core/*`
- Document detection: `modules/DocDetector/*`
- Pattern registry: `modules/PatternModules/registry/patterns/*`
- Template fields: `modules/PatternModules/registry/template_fields/*`
- Honorarios: `modules/HonorariosModule/*`
- Validator rules: `modules/ValidatorModule/*`
- Extraction support: `modules/ExtractionModule/*`

## Tests and Guards
- Pipeline tests: `tests/ObjPipeline.Tests/*`
- Document rules tests: `tests/ObjPipeline.Tests/DocumentValidationRulesTests.cs`

## Legacy Archive
- Legacy docs/rotas antigas foram removidas do repo para evitar confusao.
- Para recuperar algo, use o historico do git ou (se existir) `tmp/_local_artifacts/`.

## Rule
- Do not introduce legacy command aliases (`objpdf`, `tjpdf-cli`, `tjpdf.exe`) in active source paths.
