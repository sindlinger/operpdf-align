# PROGRESS — OBJ pipeline (front-head/back-tail)

> Rota oficial: `operpdf` (`cli/OperCli/OperCli.csproj`).
> Referências à CLI antiga neste arquivo são apenas histórico legado.

## Comando end-to-end (ativo)
- Detectar documento: `./operpdf inspect detectdoc --input <pdf|:Qn> [--only despacho|certidao|requerimento]`
- Extrair por tipo: `./operpdf pattern match --patterns <despacho|tjpb_certidao_cm|tjpb_requerimento> --inputs <lista> --detectdoc --auto --out <json>`
- Exportar CSV/XLSX: `python3 tools/scripts/export_extract.py --input outputs/extract --out-dir outputs/extract`

## Onde quebrava antes (status inicial)
- O pipeline atual roda, mas **não** segmenta por tipo/páginas (somente despacho) e não gera o JSON final consolidado por PDF com STRICT/ambíguo.
- Detecção baseada em título/keywords só cobre `despacho` (faltam CERTIDAO_CM/REQUERIMENTO_HONORARIOS e UNKNOWN explícito).
- Não existe orquestrador final único (FinalizePipeline) com agregação dos 20 campos finais + regra CM→DE (sem fallback JZ).

## Plano (5 etapas, commitáveis)
1) Auditar pipeline atual e registrar falha/ponto de parada com comando reprodutível. (feito)
2) Implementar segmentação por páginas em 3 tipos + UNKNOWN, com evidências (bookmarks + title/top/bottom + /Contents fallback). (feito)
3) Implementar orquestrador FinalizePipeline no fluxo do ObjectsPipeline para produzir JSON final único por PDF. (feito)
4) Implementar agregador dos 20 campos finais (incl. regra CM→DE→JZ) e modo STRICT. (feito)
5) Adicionar 3 testes mínimos (2 unit + 1 smoke com snapshot) e atualizar `operpdf.exe` na raiz. (feito)

## Atualização honorários (despacho + JZ)
- Regra aplicada: ESPECIE_DA_PERICIA / FATOR / VALOR_TABELADO derivam **somente** de PERITO/CPF/ESPECIALIDADE do DESPACHO + VALOR_ARBITRADO_JZ.
- Fonte do VALOR_ARBITRADO_JZ: REQUERIMENTO e/ou 1ª página do DESPACHO (não substituir DE/CM).
- Ajuste: HonorariosEnricher não usa PROFISSAO como fallback e só aplica PERCENTUAL/PARCELA quando o campo base é ADIANTAMENTO.
- Ajuste: DESPACHO passa a usar apenas VALOR_ARBITRADO_JZ; CERTIDAO_CM não usa VALOR_ARBITRADO_CM para honorários.
- Execução exemplo (10 PDFs): `./operpdf pattern match --patterns despacho --inputs :Q1-10 --detectdoc --auto --out outputs/extract/match_despacho_q1_10.json`
  - Observação: criar o diretório de saída antes de rodar (`mkdir -p ...`) para evitar cair no diretório pai.

## Atualização segmentação por bookmark
- Quando existir bookmark válido, ele define o segmento inteiro (da página do bookmark até o próximo).
- Páginas UNKNOWN dentro do intervalo **não** quebram o segmento; conflitos viram erro `DOC_TYPE_BOOKMARK_OVERRIDE` com evidência.
- Regra de tamanho: DESPACHO <= 3, REQUERIMENTO_HONORARIOS <= 3, CERTIDAO_CM <= 2. Exceder vira **apenas validação** (`DOC_TOO_LONG`), sem dividir o documento.
- Seleção de candidato (todos os tipos): se houver mais de um segmento do mesmo tipo, escolhe o **melhor score** e rebaixa os demais para UNKNOWN.
  - Score combina: número de páginas (mais páginas = melhor), densidade de texto (BodyTextOps/BodyStreamLen) e penalização por “caracteres mexidos” (muitos tokens de 1 caractere).

## Atualização regex (sem inventar)
- Finalize passou a usar **template_fields existentes** (modules/PatternModules/registry/template_fields) quando disponíveis.
- Nenhum regex novo foi criado; o fluxo reaproveita mapas já versionados no repositório.

## Atualização OBJ (2026-01-21)
- `mapfields`: regex agora tenta `WorkText` e fallback em `RawText` (whitespace normalizado) para evitar falhas por junção de palavras.
- `alignrange_fields/tjpb_despacho.yml`: padrões de PERITO/ESPECIALIDADE/ESPECIE/VALORES reforçados (baseados no template_fields) e DATA_DESPESA prioriza back_tail com âncora “João Pessoa”.
- `PathUtils`: remoção de aliases de caminho (`:D20`, `@MODEL`) no parsing de args para evitar resolução automática indesejada.
- `alignrange`: back_tail agora estende até o último text-op do stream; novo bloco `back_signature` (se existir) exportado no alignrange.
- `mapfields`: suporte a `back_signature` como banda para DATA_DESPESA.
- `mapfields`: derivados `VALOR_ARBITRADO_FINAL` e `DATA_ARBITRADO_FINAL` (com evidência do campo base) e normalização de ESPECIALIDADE para remover prefixo “Perito/Perita” e sufixo de email.
- `alignrange_fields/tjpb_despacho.yml`: PROMOVENTE agora corta antes de CPF/CNPJ mesmo sem vírgula.
- `PathUtils`: alias `:D`/`:M` reabilitado (ResolveAliasToken/ResolveModelAlias aplicados em NormalizeArgs).
- Modelo despacho: `reference/models/tjpb_despacho_model.pdf` refeito a partir de `reference/models/despachos/modelo_despacho_template.txt` com frases-âncora mais comuns (“PROCESSO Nº”, “TRATAM DE PEDIDO…”, “EM CURSO PERANTE A…”).

## Debug despacho (script)
- Script: `tools/scripts/debug_despacho.py` para inspecionar 1 DESPACHO a partir de `__final.json` + artifacts.
- Exemplo (saída parcial): em `outputs/2018198741__final.json`, o segmento DESPACHO saiu com 1 página (9..9) e erro `ALIGNRANGE_FAILED/frontA_stream_not_found`; campos finais ficaram MISSING apesar de candidatos em `mapfields`/`template`.

## Debug regex alignrange (novo)
- Script: `tools/scripts/debug_alignrange_regex.py` roda o **regex existente** em `modules/PatternModules/registry/alignrange_fields/*.yml` diretamente no `value_full_a` do alignrange.
- Exemplo (despacho real): `python3 tools/scripts/debug_alignrange_regex.py --pdf /mnt/c/Users/pichau/Desktop/geral_pdf/quarentena/2019033404.pdf --map modules/PatternModules/registry/alignrange_fields/tjpb_despacho.yml`
  - Encontrados: COMARCA, PROMOVENTE, PROMOVIDO, CPF_PERITO, VALOR_ARBITRADO_DE.
  - Não encontrados: PROCESSO_ADMINISTRATIVO, PROCESSO_JUDICIAL, VARA, PERITO, ESPECIALIDADE (ver `front_head` com “PROCESSO Nº 2019.033.404” e “Processo de nº º 0800867-86.2016.815.0201”).

## Ajustes aplicados (despacho)
- `FrontBackResolver`: quando modelo não tem página 2, o back_tail passa a alinhar usando a página do modelo (fallback não fatal).
- Regex (sem inventar profissões): adicionados padrões para:
  - PROCESSO_ADMINISTRATIVO no formato `2019.033.404`.
  - PROCESSO_JUDICIAL no formato `0800867-86.2016.815.0201`.
  - ESPECIALIDADE via “Interessado: Nome – <especialidade>”.
  - VARA com “2ª Vara ...”.
  - VALOR_ARBITRADO_DE e DATA_ARBITRADO_FINAL no back_tail com “valor solicitado”.
- Resultado (2019033404, despacho): template_fields agora extrai PA, PJ, COMARCA, VARA, PERITO, CPF, ESPECIALIDADE, VALOR_JZ, VALOR_DE e DATA_FINAL via alignrange.

## Execução operacional (2026-02-11)
- Fluxo executado para detecção + extração em lote `:Q1-20` com validação e honorários:
  - `./operpdf pattern match --patterns despacho --inputs :Q1-20 --detectdoc --auto --out outputs/extract/match_despacho_q1_20.json --no-code-preview`
  - `./operpdf pattern match --patterns tjpb_certidao_cm --inputs :Q1-20 --detectdoc --auto --out outputs/extract/match_certidao_q1_20.json --no-code-preview`
  - `./operpdf pattern match --patterns tjpb_requerimento --inputs :Q1-20 --detectdoc --auto --out outputs/extract/match_requerimento_q1_20.json --no-code-preview`
- Exportação concluída para CSV + XLSX em `outputs/extract/` via:
  - `python3 tools/scripts/export_extract.py --input outputs/extract --out-dir outputs/extract`
- Arquivos principais desta rodada:
  - `outputs/extract/match_despacho_q1_20.json|csv|xlsx`
  - `outputs/extract/match_certidao_q1_20.json|csv|xlsx`
  - `outputs/extract/match_requerimento_q1_20.json|csv|xlsx`
- Snapshot atualizado em `export/code_snapshot/` com:
  - `src/Commands/Inspect/*`
  - `modules/HonorariosModule/*`
  - `modules/ExtractionModule/TjpbDespachoExtractor/Reference/*`
  - `modules/PatternModules/registry/patterns/*`
  - `modules/PatternModules/registry/template_fields/*`
  - `operpdf.exe`
- Build executado com sucesso:
  - `./operpdf build`
  - binário atualizado em `operpdf.exe` e `export/code_snapshot/operpdf.exe`

## Execução operacional (2026-02-16)
- Rodada de verificação em lote menor `:Q23-25` (3 PDFs) para confirmar o pipeline oficial `detectdoc -> extract -> Validator + Honorarios`.
  - `./operpdf pattern match --patterns despacho --inputs :Q23-25 --detectdoc --threads 3 --timeout 240 --out outputs/extract/match_despacho_q23_25.detectdoc.head.json`
  - `./operpdf pattern match --patterns tjpb_certidao_cm --inputs :Q23-25 --detectdoc --threads 3 --timeout 240 --out outputs/extract/match_certidao_q23_25.detectdoc.head.json`
  - `./operpdf pattern match --patterns tjpb_requerimento --inputs :Q23-25 --detectdoc --threads 3 --timeout 240 --out outputs/extract/match_requerimento_q23_25.detectdoc.head.json`
- Resultado (Q23-25):
  - DESPACHO: 3/3 com core completo (PA/PJ/PERITO/CPF/PROMOVENTE/VARA/COMARCA/VALOR_JZ).
  - CERTIDAO_CM: 2/3 com campos (PA + VALOR_CM), 1/3 vazio (ausência do documento no PDF).
  - REQUERIMENTO: 3/3 com (PA + PJ + DATA_REQUISICAO).
- Export CSV + XLSX (1 linha por PDF):
  - `python3 tools/scripts/export_extract.py --input outputs/extract/match_despacho_q23_25.detectdoc.head.json --out-dir outputs/extract`
  - `python3 tools/scripts/export_extract.py --input outputs/extract/match_certidao_q23_25.detectdoc.head.json --out-dir outputs/extract`
  - `python3 tools/scripts/export_extract.py --input outputs/extract/match_requerimento_q23_25.detectdoc.head.json --out-dir outputs/extract`
- Snapshot atualizado em `export/code_snapshot/` (rsync) + `operpdf.exe`.
- Build executado com sucesso: `./operpdf build`.
- Mudanças locais que estavam degradando detecção/extração foram preservadas em `git stash stash@{0}`: "wip: dirty state before restore (2026-02-16)".
