---
name: tjpdf-obj-pipeline-finisher
description: Esta skill deve ser usada quando um agente precisa auditar e estabilizar o repositório TJPDF, consolidar o pipeline OBJ (/Contents + text ops + alignrange) e entregar os 20 campos finais (FIELDS.md) end-to-end, sem refatoração destrutiva.
---

Objetivo
Finalizar o pipeline TJPDF que já tem várias abordagens (bookmarks, análise de objetos, k-means, simhash, diffmatchpatch, heurísticas por tokens), módulo OBJ para ler o stream `/Contents` do corpo da página, `alignrange` para alinhar TextOps fixos/variáveis e um `mapfields` que “finaliza o serviço”. Entregar uma execução única reprodutível que produza os 20 campos finais com evidências e valide a saída de forma determinística.

Nomes reais do projeto (termos e artefatos)
- TJPDF: programa extrator.
- DTO base: estrutura intermediária; não é o objetivo final.
- Campos finais: os 20 campos finais (ver references/final_fields.md).
- Módulo OBJ: pipeline por objetos/operadores do PDF, especialmente `/Contents`.
- alignrange: alinha TextOps fixos/variáveis com diffmatchpatch para mapear campos.
- mapfields: finalizador que mescla fontes e calcula campos derivados.
- Documentação operacional (localizar no repo e tratar como fonte de verdade):
  - OBJETOS_PIPELINE.md
  - OBJETOS.md
  - ALIGN_RANGE.md
  - FIELDS.md
- Área de código citada nos docs:
  - TextOpsRanges/ (fixos/variáveis + helpers do alignrange)

Regras anti-bagunça
- Congelar um baseline antes de editar; evitar refatoração estética.
- Não apagar detectores/abordagens; escolher 1 caminho “default” e manter fallbacks.
- Não renomear/mover pastas sem necessidade; preferir adapters finos.
- Tornar toda extração rastreável: evidência por campo (página, método, snippet, raw_match/anchor).
- Persistir progresso: manter PROGRESS.md (ou NOTES_PIPELINE.md) com decisões, checkpoints e próximos passos.

Definition of Done (tudo obrigatório)
1) Rodar o pipeline em lote sem crash (um PDF com erro não deve parar o lote).
2) Detectar e segmentar o PDF em 3 tipos (mais UNKNOWN) com score + evidência.
   - Adotar os nomes reais do repo para esses 3 tipos (frequentemente algo como DESPACHO / REQUERIMENTO / CERTIDAO_CM).
3) Extrair os 20 campos finais e aplicar as regras de VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL.
4) Emitir JSON final que valida contra references/output_schema.json.
5) Adicionar testes mínimos:
   - unit: regra de campos derivados + normalização
   - smoke: roda 1 fixture e valida schema

Fluxo operacional
1) Triar o repositório e encontrar o pipeline atual
   - Executar: python scripts/triage_repo.py <raiz_do_repo>
   - Identificar:
     - entrypoints (CLI/scripts principais)
     - módulos de detecção/segmentação do PDF inteiro
     - módulo OBJ (/Contents) + tokenização de TextOps
     - alignrange e contratos de entrada/saída
     - mapfields (finalização) e regex por campo
     - bases/dicionários (laudos, peritos, tabela de honorários)
   - Registrar em PROGRESS.md:
     - “Caminho atual” (o que roda hoje)
     - “Caminho alvo” (orquestrador único)
     - “Defaults escolhidos” (detector default e fallbacks)

2) Fixar contratos de dados (otimizar para estabilidade)
   - Introduzir contratos mínimos (dataclasses ou TypedDict) e usar fim-a-fim:
     - PageData: page_num, dims, text_raw, tokens, contents_ops, warnings[]
     - DocSegment: doc_type, pages[], confidence, evidences[]
     - FieldResult: name, value, confidence, page_num, method, snippet, raw_match, warnings[]
   - Adaptar módulos existentes com adapters para produzir/consumir esses contratos, sem reescrever lógica.

3) Consolidar a detecção/segmentação em um orquestrador (default + fallback)
   - Criar DetectorOrchestrator:
     - executar o detector default primeiro (o mais estável em teste)
     - fazer fallback para outros detectores quando confidence < limiar
   - Retornar sempre:
     - doc_type (3 tipos reais do repo + UNKNOWN)
     - confidence (0..1)
     - evidences (bookmarks, anchors, hits de token, cluster simhash/kmeans, etc.)
   - Converter outputs heterogêneos para um formato único de evidência.

4) Padronizar OBJ e a chamada do alignrange
   - Localizar e seguir os docs:
     - OBJETOS_PIPELINE.md (op_range + anchors + ValueFull)
     - ALIGN_RANGE.md (fluxo novo: fixos/variáveis + diffmatchpatch)
   - Garantir 1 função canônica que gera a representação por página:
     - ler `/Contents`
     - extrair operadores textuais e operandos
     - gerar token stream estável (fixo/variável)
   - Garantir que alignrange seja chamado em 1 integração única com input estável e retorno de campos com evidência.

5) Implementar extratores por tipo e finalizar em mapfields
   - Para cada tipo (3 tipos), definir:
     - required fields e optional fields
     - precedência (OBJ/alignrange → regex → heurísticas)
   - Implementar Extractor(doc_type) → lista de FieldResult.
   - Implementar/ajustar mapfields para:
     - mesclar FieldResult por precedência e confiança
     - normalizar valores (datas, moeda, ids)
     - calcular campos derivados (ver references/final_fields.md)
     - registrar campos obrigatórios faltantes em errors[]
     - preservar evidências (para debug)

6) Validar e logar
   - Adicionar logging estruturado por PDF e por etapa (ingest/classify/segment/extract/finalize).
   - Adicionar modo “debug artifacts” para salvar:
     - texto por página
     - tokens/ops
     - diffs do alignrange
     - JSON intermediário
   - Validar a saída final com:
     - python scripts/validate_output.py --json <output.json>

7) Testar (mínimo que desbloqueia entrega)
   - Testes unitários:
     - regra VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL (scripts/derive_fields.py como referência)
     - normalização de moeda/data
   - Smoke test:
     - rodar 1 fixture PDF e validar schema (ou mock de PageData se não houver fixture)

Exemplos com campos reais
- Regra de VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL
  - Se VALOR_ARBITRADO_CM existir: final = CM; data_final = decisão do Conselho (certidão CM).
  - Senão, se VALOR_ARBITRADO_DE existir: final = DE; data_final = data do despacho.
  - Senão: final = JZ; data_final = data do despacho/requerimento.

- Evidência para PERITO (exemplo de FieldResult)
  - name: PERITO
  - value: “FULANO DE TAL”
  - page_num: 3
  - method: “obj_alignrange”
  - snippet: “Perito: FULANO DE TAL”
  - raw_match: range de tokens ou grupos do regex

Padrões de busca quando o caminho do arquivo é desconhecido
- Nomes de arquivo:
  - OBJETOS_PIPELINE.md, ALIGN_RANGE.md, OBJETOS.md, FIELDS.md, FLEXIBLE_ROADMAP.md
- Palavras-chave:
  - “alignrange”, “TextOps”, “/Contents”, “diffmatchpatch”, “ValueFull”, “op_range”, “anchors”, “mapfields”
- Entry-points:
  - “argparse”, “click”, “main()”, “__name__ == '__main__'”, “extract_pdf”

Recursos incluídos
- references/final_fields.md: lista oficial dos 20 campos finais e regras.
- references/obj_module_docs.md: mapa de integração do módulo OBJ.
- references/codex_prompt.md: prompt operacional para Codex com regras anti-bagunça.
- references/output_schema.json: schema para validação determinística.
- scripts/triage_repo.py: triagem para achar entrypoint e módulos.
- scripts/validate_output.py: validação do JSON final.
- scripts/derive_fields.py: implementação de referência para a regra de campos derivados.
