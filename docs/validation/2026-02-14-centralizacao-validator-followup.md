# Diario - 2026-02-14 - follow-up centralizacao detector/validator

## Escopo desta rodada
- Ajustar validacao documental de `requerimento_honorarios` no modulo central.
- Reexecutar detecao/extracao no lote `Q1-20`.
- Exportar artefatos consolidados (JSON/CSV/XLSX/TSV).
- Atualizar snapshot responsavel e build `operpdf.exe`.

## Alteracoes de codigo
- `modules/ValidatorModule/DocumentValidationRules.cs`
  - `IsRequerimentoFromTopBody(...)`:
    - ampliado conjunto de semanticas aceitas de requerimento (pagamento/requisicao de honorarios periciais);
    - remove bloqueio duro por marcador explicito quando semantica forte existe;
    - remove bloqueio direto por ofício no caminho de validacao do requerimento.
  - `IsTargetStrongPass(...)` (ramo requerimento):
    - removido filtro `IsLikelyOficio(...)` para nao matar casos reais de expediente/oficio de pagamento.

## Execucao
- Build release:
  - `dotnet build operpdf.sln -c Release`
- Publish Windows:
  - `dotnet publish cli/OperCli/OperCli.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64`
- Extracao principal:
  - despacho: `outputs/extract/final5_q1_20_despacho.json`
  - certidao (consolidado por chunks): `outputs/extract/final5_q1_20_certidao.json`
  - requerimento: `outputs/extract/final5_q1_20_requerimento.json`

## Resultado consolidado (final5)
- Extracao por tipo (page1 > 0):
  - despacho: `20/20`
  - certidao: `10/20`
  - requerimento: `20/20`
- Validador de campos obrigatorios (recorte final5):
  - despacho: `20 OK`
  - certidao: `10 OK`, `10 NO_DOC`
  - requerimento: `20 OK`

## Artefatos gerados/atualizados
- Extracao:
  - `outputs/extract/final5_q1_20_despacho.json`
  - `outputs/extract/final5_q1_20_despacho.csv`
  - `outputs/extract/final5_q1_20_despacho.xlsx`
  - `outputs/extract/final5_q1_20_certidao.json`
  - `outputs/extract/final5_q1_20_certidao.csv`
  - `outputs/extract/final5_q1_20_certidao.xlsx`
  - `outputs/extract/final5_q1_20_requerimento.json`
  - `outputs/extract/final5_q1_20_requerimento.csv`
  - `outputs/extract/final5_q1_20_requerimento.xlsx`
- Validacao:
  - `outputs/extract/final5_extraction_validator_q1_20.tsv`
  - `outputs/extract/final5_extraction_validator_q1_20.csv`
  - `outputs/extract/final5_extraction_validator_q1_20.xlsx`
- Matriz de detector:
  - `outputs/extract/final5_detectdoc_matrix_q1_20.tsv`
  - `outputs/extract/final5_detectdoc_matrix_q1_20.csv`
  - `outputs/extract/final5_detectdoc_matrix_q1_20.xlsx`
- Detect requerimento atualizado:
  - `outputs/extract/final5_detect_requerimento.tsv`

## Snapshot responsavel
- Atualizado em `export/code_snapshot/` com:
  - `src/Commands/Inspect/*`
  - `modules/HonorariosModule/*`
  - `modules/ExtractionModule/TjpbDespachoExtractor/Reference/*`
  - `modules/PatternModules/registry/patterns/*`
  - `modules/PatternModules/registry/template_fields/*`
  - `modules/ValidatorModule/*`
  - `operpdf.exe` (+ runtime files)
