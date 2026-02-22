# Pipeline TextOpsAlign — Mapeamento de Execução (Orquestração por Etapas)

## 1) Entrada e despacho de comando

### 1.1 CLI principal
- Arquivo: `cli/OperCli/Program.cs:45`
- Função: `Main(string[] args)`
- Finalidade: roteia para comandos `textopsalign-*`, `textopsvar-*`, `textopsfixed-*` e `textopsrun-*`.

### 1.2 Comandos tipados (forçam doc + alias de modelo)
- Arquivo: `cli/OperCli/Program.cs:63`, `cli/OperCli/Program.cs:93`, `cli/OperCli/Program.cs:121`
- Função chamada: `ForceDocAndTypedModel(rest, forcedDoc, typedModelAlias)`
- Entrada: args do usuário
- Saída enviada ao módulo: args normalizados com:
  - `--doc` forçado por tipo (`despacho`, `certidao_conselho`, `requerimento_honorarios`)
  - primeiro input garantido como alias tipado (`@M-DESP`, `@M-CER`, `@M-REQ`)

### 1.3 Resolução de alias e índices
- Arquivo: `modules/Core/Utils/PathUtils.cs:181`
- Cadeia:
  - `ResolveModelVariable` -> `ResolveTypedModelAliasToken` -> `ResolveTypedModelAlias` -> `ResolveTypedModelDirs`
  - `ResolveIndexVariable` -> `TryResolveIndexDir`
- Regras críticas:
  - `@MODEL` global desativado (comentário explícito em `modules/Core/Utils/PathUtils.cs:204`)
  - só aliases tipados `@M-*`
  - diretórios de modelo por tipo via `OBJPDF_ALIAS_M_DES_DIR`, `OBJPDF_ALIAS_M_CER_DIR`, `OBJPDF_ALIAS_M_REQ_DIR`

---

## 2) Comando central orquestrador (`textopsrun-*`)

### 2.1 Entrada
- Arquivo: `cli/OperCli/Program.cs:139`
- Comandos:
  - `textopsrun-despacho`
  - `textopsrun-certidao`
  - `textopsrun-requerimento`

### 2.2 Núcleo do orquestrador
- Arquivo: `cli/OperCli/Program.cs:186`
- Função: `ExecuteOrchestratedRun(string[] rest, string forcedDoc, string typedModelAlias, string docLabel)`
- Etapas internas do orquestrador:
  1. normaliza args via `ForceDocAndTypedModel`
  2. cria diretórios `run/` e `run/io/`
  3. abre sessão `run/io/<timestamp>__<doc>/`
  4. executa em sequência:
     - `textopsalign-<doc>`
     - `textopsvar-<doc>`
     - `textopsfixed-<doc>`
  5. para cada chamada salva:
     - `__request.json` (ida)
     - `__response.json` (volta)
     - `__stdout.log`
     - `__stderr.log`
  6. gera `manifest.json` e `run/io/latest_session.txt`

### 2.3 Execução de cada subcomando
- Arquivo: `cli/OperCli/Program.cs:303`
- Função: `ExecuteOrchestratedStep(string commandName, string[] args)`
- Chamadas reais:
  - `ObjectsTextOpsAlign.Execute(args)` para `textopsalign-*`
  - `ObjectsTextOpsAlign.ExecuteWithMode(args, VariablesOnly)` para `textopsvar-*`
  - `ObjectsTextOpsAlign.ExecuteWithMode(args, FixedOnly)` para `textopsfixed-*`
- Retorno capturado:
  - `exit_code`
  - `stdout`
  - `stderr`

---

## 3) Pipeline funcional (módulo `ObjectsTextOpsAlign`)

### 3.0 Entrada do pipeline
- Arquivo: `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1142`
- Função: `ExecuteWithMode(string[] args, OutputMode outputMode)`
- Entradas principais:
  - `--inputs` (2 PDFs esperados)
  - `--doc`
  - `run N-M` / `--run`
  - `--log`, `--probe`, `--step-output`
- Retornos:
  - `LastExitCode` + `Environment.ExitCode`
  - saída de console
  - opcionalmente arquivos por etapa (`SaveStageOutputs`)

### 3.1 Pré-validação de inputs e isolamento
- Arquivos/funções:
  - `DeduplicateInputs` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2124`
  - `TryCollapseModelCandidatesForSingleTarget` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1539`
  - `AreSameFilePath` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2155`
- Proteções:
  - exige mínimo de 2 inputs (`src/Commands/Inspect/ObjectsTextOpsAlign.cs:1213`)
  - bloqueia modelo==alvo (`src/Commands/Inspect/ObjectsTextOpsAlign.cs:1683`)
  - bloqueia ambiguidade de papéis (tem que ser 1 template + 1 alvo) (`src/Commands/Inspect/ObjectsTextOpsAlign.cs:1699`)
  - na seleção automática ignora candidato de modelo igual ao alvo (`src/Commands/Inspect/ObjectsTextOpsAlign.cs:1579`)

---

## 4) Etapas obrigatórias (1..8)

## Etapa 1/8 — Detecção e seleção de objetos
- Chamador: `ExecuteWithMode`
- Código de seleção:
  - `ResolveSelection(...)` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1509`
  - `TryResolveDespachoSelection(...)` (interna) para rota automática de despacho
- Módulos declarados no payload:
  - `Obj.DocDetector + ObjectsFindDespacho + ContentsStreamPicker`
- Entrada:
  - `pdf modelo`, `pdf alvo`, hints de página/objeto
- Saída:
  - `page/obj/source` para A e B
  - stage payload com `modelo_pdf`, `pdf_alvo_extracao`, `modelo_sel`, `alvo_sel`

## Etapa 2/8 — Alinhamento textual
- Chamador: `ExecuteWithMode`
- Chamada principal:
  - `ObjectsTextOpsDiff.ComputeAlignDebugForSelection(...)`
  - em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1767`
- Implementação do alinhamento:
  - `modules/TextOpsRanges/ObjectsTextOpsDiff.AlignDebug.cs:76`
- Entrada enviada:
  - paths A/B
  - seleção `PageObjSelection`
  - parâmetros de alinhamento (`minSim`, `band`, `minLenRatio`, etc)
- Retorno recebido:
  - `AlignDebugReport` contendo:
    - `BlocksA/BlocksB`
    - `Anchors`
    - `Alignments`
    - `FixedPairs`
    - `RangeA/RangeB`
    - `HelperDiagnostics`

## Etapa 3/8 — Parser YAML (preparação + recorte + parsing)
- Chamador: `ExecuteWithMode` -> `BuildExtractionPayload(...)`
- Entrada:
  - `AlignDebugReport` + `aPath/bPath` + `docKey`
- Subetapas internas:
  1. preparação (`DocumentValidationRules`) em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2680`
  2. recorte `value_full/op_range` via `BuildValueFullFromBlocks` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2750`
  3. parser YAML via `ObjectsMapFields.TryExtractFromInlineSegments(...)`
     - chamada em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2616`
     - implementação em `src/Commands/Inspect/ObjectsMapFields.cs:156`
- Retorno:
  - `CompactExtractionOutput` (`PdfA.Values/Fields`, `PdfB.Values/Fields`, `MapPath`, `Band`)

## Etapa 4/8 — Honorários
- Chamador: `BuildExtractionPayload`
- Módulo:
  - `Obj.Honorarios.HonorariosFacade`
- Chamadas:
  - `ApplyProfissaoAsEspecialidade(...)` (`modules/HonorariosModule/HonorariosFacade.cs:7`)
  - `ApplyBackfill(...)` (`modules/HonorariosModule/HonorariosFacade.cs:12`)
- Entrada:
  - dicionário de valores extraídos por lado
- Retorno:
  - `HonorariosBackfillResult` por lado
  - valores potencialmente alterados + derivados

## Etapa 5/8 — Reparador
- Chamador: `BuildExtractionPayload`
- Módulo:
  - `Obj.ValidationCore.ValidationRepairer`
- Chamada:
  - `ApplyWithValidatorRules(...)` em `modules/ValidationCore/Adapters/ValidationRepairer.cs:24`
- Entrada:
  - valores correntes + `outputDocType` + `PeritoCatalog`
- Retorno:
  - `RepairOutcome` (`Applied`, `Ok`, `Reason`, `ChangedFields`, `LegacyMirror*`)

## Etapa 6/8 — Validador
- Chamador: `BuildExtractionPayload`
- Módulo:
  - `Obj.ValidatorModule.ValidatorFacade`
- Chamadas:
  - `GetPeritoCatalog(...)` (`modules/ValidatorModule/ValidatorFacade.cs:8`)
  - `ApplyAndValidateDocumentValues(...)` (`modules/ValidatorModule/ValidatorFacade.cs:56`)
- Entrada:
  - valores pós-honorários/reparador
- Retorno:
  - `ok_a`, `ok_b`, `ok_pair`, `reason*`, `changedFields`
  - aplicação de política `strict money`

## Etapa 7/8 — Probe
- Chamador: `ExecuteWithMode` (após `BuildExtractionPayload`)
- Módulo:
  - `Obj.RootProbe.ExtractionProbeModule`
- Chamada:
  - `ExtractionProbeModule.Run(...)` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1964`
  - implementação em `probe/ExtractionProbeModule.cs:15`
- Entrada:
  - `pdfPath`, `page`, `values` do lado alvo (`pdf_a` ou `pdf_b`), `sideLabel`
- Retorno:
  - payload com `status`, `found/missing`, itens por campo e método de match

## Etapa 8/8 — Persistência e resumo
- Chamador: `ExecuteWithMode`
- Persistência de etapas:
  - `SaveStageOutputs(...)` em `src/Commands/Inspect/ObjectsTextOpsAlign.cs:498`
- Saídas:
  - logs de pipeline no console
  - JSON de etapas por execução (quando `step_output_save=true`)
  - saída final `parsed`, `value_flow`, `module_status`, etc

---

## 5) Esquema visual (chamada -> retorno)

```text
Program.Main
  ├─ textopsrun-<tipo> -> ExecuteOrchestratedRun
  │    ├─ step1: textopsalign-<tipo> -> ObjectsTextOpsAlign.Execute
  │    ├─ step2: textopsvar-<tipo>   -> ObjectsTextOpsAlign.ExecuteWithMode(VariablesOnly)
  │    └─ step3: textopsfixed-<tipo> -> ObjectsTextOpsAlign.ExecuteWithMode(FixedOnly)
  │
  │    para cada step:
  │      request.json (ida) + stdout/stderr + response.json (volta)
  │
  └─ comandos diretos textopsalign/textopsvar/textopsfixed
       └─ ExecuteWithMode
            ├─ Etapa 1: seleção A/B
            ├─ Etapa 2: ComputeAlignDebugForSelection -> AlignDebugReport
            ├─ Etapa 3: TryExtractFromInlineSegments  -> CompactExtractionOutput
            ├─ Etapa 4: HonorariosFacade              -> HonorariosBackfillResult
            ├─ Etapa 5: ValidationRepairer            -> RepairOutcome
            ├─ Etapa 6: ValidatorFacade               -> ok/reason/changed
            ├─ Etapa 7: ExtractionProbeModule         -> probe payload
            └─ Etapa 8: persistência/resumo
```

---

## 6) Contratos de IO gravados pelo orquestrador em `run/io`

- `run/io/<session>/01_textopsalign-<doc>__request.json`
- `run/io/<session>/01_textopsalign-<doc>__stdout.log`
- `run/io/<session>/01_textopsalign-<doc>__stderr.log`
- `run/io/<session>/01_textopsalign-<doc>__response.json`
- idem para `02_textopsvar-...` e `03_textopsfixed-...`
- `run/io/<session>/manifest.json`
- `run/io/latest_session.txt`

Campos principais:
- request: `sequence`, `command`, `module`, `forced_doc`, `typed_model_alias`, `args`, `started_utc`
- response: `exit_code`, `duration_ms`, `stdout_path`, `stderr_path`, `stdout_len`, `stderr_len`

---

## 7) Artefatos de alinhamento (quem escreve, quem lê, e estado atual)

### 7.1 Quem escreve `alignrange`/`textops_align`
- `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2041`
  - grava `outputs/align_ranges/<prefix>__textops_align.json` (modo `textopsalign`/`OutputMode.All`).
- `src/Commands/Inspect/ValidationPipeline/AlignRangeStage.cs:489`
  - grava `outputs/align_ranges/<base>__textops_align.json`.
- `src/Commands/Inspect/ValidationPipeline/PatternStage.cs:302`
  - gera saída alinhada em `outputs/align_ranges/...__textops_align.json`.

### 7.2 Quem lê `alignrange` por arquivo
- `src/Commands/Inspect/ObjectsMapFields.cs:21`
  - comando `mapfields` lê `--alignrange <arquivo>` + `--map <yaml>` e extrai campos.
- `src/Commands/Inspect/ValidationPipeline/AlignRangeStage.cs:499`
  - chama `mapfields` com arquivo de alignrange quando o estágio pede parse por arquivo.

### 7.3 Fluxo usado hoje no `textopsalign-*` (pipeline principal)
- `src/Commands/Inspect/ObjectsTextOpsAlign.cs:2623`
  - usa `ObjectsMapFields.TryExtractFromInlineSegments(...)`.
- Isso consome **direto da memória** (`value_full/op_range` do `AlignDebugReport`) sem depender de arquivo intermediário.

### 7.4 Motivo da troca para inline no pipeline principal
- Eliminar dependência de ordem de gravação/leitura em disco.
- Reduzir risco de corrida/colisão de arquivo quando há execução orquestrada.
- Permitir rastreio por etapa em memória (`stage payload`) antes da persistência final.

### 7.5 Como “retornar” ao consumo por arquivo (quando necessário)
- Caminho já existente e suportado:
  1. Executar `textopsalign-*` para gerar `outputs/align_ranges/...__textops_align.json`.
  2. Executar `mapfields --alignrange <arquivo> --map <yaml>`.
- Esse modo **não foi removido**; apenas deixou de ser o caminho default do pipeline principal.

---

## 8) Situação de `objdiff` e `textopsdiff`

- O comando `run` (textopsrun-*) **não** chama `objdiff` legado.
  - Ele chama exclusivamente `ObjectsTextOpsAlign` em três modos (`align/var/fixed`).
  - Referência: `cli/OperCli/Program.cs:206`, `cli/OperCli/Program.cs:303`.
- O núcleo atual de alinhamento textual usado no pipeline é `ObjectsTextOpsDiff.ComputeAlignDebugForSelection`.
  - Referência: `src/Commands/Inspect/ObjectsTextOpsAlign.cs:1767`.
- O módulo legado `ObjectsTextOpsDiff.Execute(..., DiffMode.Both)` ainda existe e é usado em partes da `ValidationPipeline` para diagnóstico/recorte.
  - Referência: `src/Commands/Inspect/ValidationPipeline/FindDespachoStage.cs:612`, `src/Commands/Inspect/ValidationPipeline/FindDespachoStage.cs:693`.
