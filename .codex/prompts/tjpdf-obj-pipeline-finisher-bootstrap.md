$tjpdf-obj-pipeline-finisher

Objetivo: finalizar o pipeline TJPDF end-to-end neste repositorio sem refatoracao estetica.

Regras:
- Antes de alterar codigo, identifique o entrypoint real e descreva o fluxo atual.
- Preserve os modulos existentes (OBJ/alignrange/TextOpsRanges/mapfields/regex/db). Escolha um "default path" e mantenha fallbacks.
- Produza saida JSON consistente com evidencia por campo e testes (smoke + unit minimo).

Primeiro passo:
- Liste os entrypoints e onde ficam: deteccao de doc, deteccao por pagina, OBJ (/Contents), alignrange, mapfields e regex de campos.
