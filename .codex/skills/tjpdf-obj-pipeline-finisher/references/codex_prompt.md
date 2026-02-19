# Prompt operacional para Codex (TJPDF / OBJ / mapfields)

Copiar e colar este texto como a primeira mensagem do Codex ao dar acesso ao repositório.

---

Você é o Codex atuando como engenheiro responsável por FINALIZAR o pipeline TJPDF. O repositório já contém:

- Detecção/segmentação do PDF por múltiplas abordagens (bookmarks, análise de objetos, k-means, simhash, diffmatchpatch, heurísticas por tokens).
- Módulo OBJ (/Contents text ops) para ler stream do corpo da página.
- `alignrange` (TextOps fixos/variáveis + diffmatchpatch) para alinhar o “PDF da vez” com o “modelo” e retornar JSON campo por campo.
- `mapfields` para finalizar e derivar os campos finais.

OBJETIVO ÚNICO
- Fazer o pipeline rodar end-to-end em batch sem crash.
- Segmentar em 3 tipos (adotar os nomes reais do repo; frequentemente algo como DESPACHO / REQUERIMENTO / CERTIDAO_CM).
- Extrair e preencher os 20 campos finais do TJPDF listados em `FIELDS.md` (e em `references/final_fields.md`).
- Aplicar regras:
  - `VALOR_ARBITRADO_FINAL` / `DATA_ARBITRADO_FINAL` conforme `references/final_fields.md`.
  - `DATA_REQUISICAO` vem do requerimento de pagamento de honorários.
- Emitir JSON final com evidências por campo e validar contra `references/output_schema.json`.

REGRAS ANTI-BAGUNÇA (obrigatórias)
- Não reescrever do zero.
- Não apagar detectores/abordagens; escolher 1 default + manter fallbacks.
- Não fazer refatoração cosmética.
- Antes de editar código: produzir um plano com a lista de arquivos que serão tocados e os testes que serão adicionados.
- Implementar em passos pequenos e sempre rodar testes/validação antes de seguir.

PASSO 0 — TRIAGEM
- Rodar `python scripts/triage_repo.py .` e localizar:
  - entrypoint do pipeline (CLI/script principal)
  - módulo OBJ / TextOpsRanges / alignrange
  - `mapfields`
  - docs: `OBJETOS_PIPELINE.md`, `OBJETOS.md`, `ALIGN_RANGE.md`, `FIELDS.md`
- Criar/atualizar `PROGRESS.md` com:
  - caminho atual (o que executa hoje)
  - caminho alvo (orquestrador único)
  - decisões: detector default, formato de output, fixtures

CONTRATOS DE DADOS (não negociar)
- Introduzir contratos mínimos (dataclasses ou TypedDict) e usá-los fim-a-fim:
  - PageData(page_num, dims, text_raw, tokens, contents_ops, warnings)
  - DocSegment(doc_type, pages, confidence, evidences)
  - FieldResult(name, value, confidence, page_num, method, snippet, raw_match, warnings)
- Adaptar módulos existentes com “adapters” para que produzam/consumam estes contratos sem reescrever lógica interna.

PIPELINE CONSOLIDADO (orquestrador único)
- Implementar um orquestrador que faça:
  1) Ingestão por página (texto + OBJ quando necessário)
  2) Classificação/segmentação do PDF em 3 tipos
  3) Extração por segmento:
     - priorizar OBJ/alignrange para campos críticos
     - usar regex/heurísticas como fallback
  4) Finalização (`mapfields`) + normalização
  5) Derivação de campos finais (VALOR/DATA_ARBITRADO_FINAL)
  6) Emissão do JSON final com evidências

DETECÇÃO (default + fallback)
- Escolher 1 detector como default (o mais estável em testes).
- Encapsular os demais como estratégias de fallback quando confidence < limiar.
- Sempre retornar doc_type + confidence + evidences (com justificativa).

EXTRAÇÃO (por tipo)
- Para cada tipo, definir:
  - required fields
  - optional fields
  - precedência de fontes (alignrange → regex → heurísticas)
- Produzir FieldResult com evidência por campo.

VALIDAÇÃO E TESTES
- Validar output final com:
  - `python scripts/validate_output.py --json <output.json>`
- Adicionar testes mínimos:
  - unit: derivação VALOR/DATA_ARBITRADO_FINAL
  - unit: normalização (moeda/data)
  - smoke: roda 1 fixture e valida schema

ENTREGÁVEIS
- Pipeline rodando end-to-end sem crash.
- JSON final validado e com evidências.
- Testes mínimos + comando único para rodar.
- `PROGRESS.md` atualizado com decisões e checkpoints.
