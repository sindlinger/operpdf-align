# Documentação do módulo OBJ (operacional)

Esta referência resume, em termos práticos, como a documentação do módulo OBJ deve ser usada para finalizar o pipeline.

## O que é o módulo OBJ

O módulo OBJ é a abordagem baseada em objetos/operadores do PDF, em particular o stream de `/Contents` do corpo da página. O objetivo é:

- Identificar operadores textuais (text ops) e seus operandos
- Diferenciar partes fixas vs variáveis (tokenização)
- Encontrar anchors e ranges (op_range)
- Rodar o `alignrange` para alinhar “PDF da vez” vs “modelo”
- Produzir JSON campo-por-campo (com evidências) para o `mapfields` finalizar os campos

## Arquivos citados como fonte de verdade

Localizar estes arquivos no repositório e tratá-los como documentação operacional:

- `OBJETOS_PIPELINE.md` — pipeline por operadores (op_range + anchors + ValueFull)
- `OBJETOS.md` — detalhamento do trabalho em textops (fixo/variável, anchors, regras)
- `ALIGN_RANGE.md` — versão nova do fluxo:
  - como o alignrange chama o módulo de textops variáveis e fixos
  - como identifica intervalos do PDF “da vez” e do modelo
  - como alinha operadores textuais do stream de `/Contents` (usando diffmatchpatch)
  - como captura a parte variável e separa da fixa
  - como retorna JSON campo por campo para o `mapfields`
- `FIELDS.md` — lista oficial dos campos finais

## Pasta de código relacionada

Os docs mencionam uma realocação:

- `TextOpsRanges/` — fixos/variáveis + alignrange helpers (código realocado)

## Como integrar no pipeline consolidado (ordem)

1) Ingestão por página
- Extrair metadados da página (número, dimensões)
- Extrair texto bruto (para fallback/regex)
- Extrair representação OBJ (operadores do `/Contents`) quando necessário

2) Segmentação por documento (PDF inteiro)
- Rodar detecção de tipo por PDF/páginas (bookmarks, objetos, clusters, simhash/diffmatch, etc.)
- Produzir `DocSegment` com doc_type e pages[]

3) Extração por segmento
- Para segmentos que exigem precisão (campos críticos), priorizar OBJ:
  - gerar tokens/ops em `TextOpsRanges`
  - rodar `alignrange`
  - obter JSON por campo (com evidências)

4) Finalização (`mapfields`)
- Mesclar evidências OBJ + regex + heurísticas
- Normalizar valores
- Aplicar regras derivadas (VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL)
- Emitir JSON final validável

## Padrões de busca (quando caminhos são desconhecidos)

Procurar por:
- “TextOpsRanges”
- “alignrange”
- “diffmatchpatch”
- “/Contents”
- “ValueFull”
- “op_range”
- “anchors”
- “mapfields”
