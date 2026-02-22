# PROMPT OPERACIONAL — MAPA DE MÓDULOS E OBJETIVO

Você está no repositório `operpdf-textopsalign` e deve manter o pipeline completo funcional, sem pular módulos.

## Objetivo Geral
Garantir extração confiável para:
1. Despacho
2. Certidão CM
3. Requerimento

Sem valor inventado, sem falso positivo silencioso, com rastreabilidade por campo (`source`, `op_range`, `obj`, `module`).

## Diretório de Trabalho
- Repositório: `/mnt/c/git/operpdf-textopsalign`

## Pipeline Obrigatório (8 etapas)
1. Detecção e seleção
2. Alinhamento
3. Parser YAML (campos)
4. Honorários
5. Reparador
6. Validador
7. Probe
8. Persistência e resumo

## Objetivo Complementar Obrigatório (Comando Único)
Implementar e manter um comando único que execute o pipeline completo fim-a-fim e retorne saída organizada por etapa, com destaque claro entre elas.

Requisitos desse comando único:
1. Executar as 8 etapas em sequência, sem pular módulo obrigatório.
2. Exibir cada etapa com bloco próprio e título explícito (etapa atual e próxima).
3. Mostrar o resultado de cada etapa antes de seguir para a próxima.
4. Mostrar, obrigatoriamente, análise/intervenção de:
   - Honorários (`Obj.Honorarios.HonorariosFacade`)
   - Reparador (`Obj.ValidationCore.ValidationRepairer`)
   - Validador (`Obj.ValidatorModule.ValidatorFacade`)
   - Probe (`Obj.RootProbe.ExtractionProbeModule`)
5. Exibir status por módulo (`ok`, `fail`, `skipped`) com motivo textual.
6. Exibir resultado final consolidado do alvo com 100% de transparência de origem por campo.

## Mapa de Módulos (arquivo + função + objetivo)

### 1) Orquestração do comando e pipeline E2E
- Arquivo: `src/Commands/Inspect/ObjectsTextOpsAlign.cs`
- Faz: parse de parâmetros, seleção A/B, execução de etapas 1..8, saída humana e JSON.
- Objetivo: controlar o fluxo inteiro sem quebrar integração entre módulos.

### 2) Detecção e seleção de objeto/página
- Arquivos:
  - `modules/DocDetector/Detectors/BookmarkDetector.cs`
  - `modules/DocDetector/Detectors/ContentsPrefixDetector.cs`
  - `modules/DocDetector/Detectors/HeaderLabelDetector.cs`
  - `modules/DocDetector/Detectors/LargestContentsDetector.cs`
  - `modules/DocDetector/Detectors/ContentsStreamPicker.cs`
  - `src/Commands/Inspect/ValidationPipeline/FindDespachoStage.cs`
  - `src/Commands/Inspect/ValidationPipeline/DetectDocStage.cs`
- Faz: encontra página e stream/objeto corretos para iniciar extração.
- Objetivo: selecionar corretamente o objeto textual do documento alvo.

### 3) Alinhamento textual (DMP + heurísticas)
- Arquivos:
  - `modules/Align/ObjectsTextOpsDiff.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignDebug.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.BuildBlockAlignments.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.BuildAnchorPairsExplicit.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.BuildAnchorPairsAuto.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.AlignHelper.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignRange.WordSimilarity.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.Pattern.cs`
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs`
- Faz: gera blocos, anchors, fixed/variable/gap, `rangeA/rangeB`.
- Objetivo: entregar recorte confiável (`op_range`) para o parser de campos.

### 4) Parser YAML de campos
- Arquivos:
  - `src/Commands/Inspect/ObjectsMapFields.cs`
  - `modules/PatternModules/registry/alignrange_fields/tjpb_despacho.yml`
  - `modules/PatternModules/registry/alignrange_fields/tjpb_certidao.yml`
  - `modules/PatternModules/registry/alignrange_fields/tjpb_requerimento.yml`
- Faz: extrai campos por regex/regra com base no texto alinhado.
- Objetivo: produzir `values` + `fields` com metadados por campo.

### 5) Honorários (enriquecimento derivado)
- Arquivo: `modules/HonorariosModule/HonorariosFacade.cs`
- Faz: backfill/derivações de valores (fator, tabelado, complementos).
- Objetivo: completar campos derivados sem sobrescrever indevidamente parser explícito.

### 6) Reparador
- Arquivo: `modules/ValidationCore/Adapters/ValidationRepairer.cs`
- Faz: correções pós-parser/honorários com regras de saneamento.
- Objetivo: reduzir inconsistências mantendo rastreio de alteração.

### 7) Validador
- Arquivo: `modules/ValidatorModule/ValidatorFacade.cs`
- Faz: validação documental e aplicação de política estrita por tipo de documento.
- Objetivo: bloquear resultado inválido com `ok/reason` explícitos.

### 8) Probe (checagem de presença no PDF alvo)
- Arquivo: `probe/ExtractionProbeModule.cs`
- Faz: verifica se valores finais realmente aparecem no PDF alvo.
- Objetivo: evidenciar `found/missing` por campo e método de match.

### 9) Persistência/retorno
- Arquivo: `modules/Core/Utils/ReturnUtils.cs`
- Faz: modo retorno e gravação de saída estruturada.
- Objetivo: garantir persistência reprodutível para auditoria.

## Regra de Funcionamento Obrigatória
Considere sucesso somente se todos os módulos do pipeline forem executados quando aplicáveis:
- `Obj.Honorarios.HonorariosFacade`
- `Obj.ValidationCore.ValidationRepairer`
- `Obj.ValidatorModule.ValidatorFacade`
- `Obj.RootProbe.ExtractionProbeModule`

Se algum módulo não executar, deve sair como `skipped` com motivo explícito (nunca silencioso).

## Critério de Aceite Técnico
1. Build sem erro.
2. Comandos mínimos executando:
   - `./align.exe textopsalign-despacho --inputs @MODEL --inputs :Q22 --probe`
   - `./align.exe textopsvar-despacho --inputs @MODEL --inputs :Q22 --probe`
   - `./align.exe textopsfixed-despacho --inputs @MODEL --inputs :Q22 --probe`
3. Saída final com:
   - campos extraídos do alvo,
   - `validator.ok/reason`,
   - `probe found/missing`,
   - rastreio por campo (`source`, `op_range`, `obj`, `module`).

## Regra de Segurança de Dados
Não usar placeholder hardcoded para preencher campo real.
Se não encontrou, marque vazio com status/razão; não invente valor.
