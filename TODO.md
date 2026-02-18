• Problema (em português)

  - A regressão é em src/Commands/Inspect/ObjectsPipeline.cs na função ClassifyDocKey: ela agora só olha TitleKey/Title/
    TitleNormalized. Quando o título não vem (comum quando o /Contents não tem marcador), ela retorna string vazia, o
    segmentador descarta o doc e o pipeline pula extração. Isso quebra PDFs que só têm o rótulo no corpo. O correto é
    usar fallback do corpo (BodyPrefix/BodySuffix/ContentsPrefix/HeaderLabel) sem usar topo/rodapé e, se nada bater,
    retornar UNKNOWN (não vazio).

  Prompt para o Codex consertar

  Você está em /mnt/c/git/tjpdf/OBJ. Corrija a regressão apontada na review.

  Contexto:
  - A função ClassifyDocKey em src/Commands/Inspect/ObjectsPipeline.cs foi alterada para só usar TitleKey/Title/
  TitleNormalized.
  - Quando TitleKey não vem, o doc_type fica vazio e o segmentador descarta o documento, pulando a extração.

  Tarefas:
  1) Em src/Commands/Inspect/ObjectsPipeline.cs, restaure fallback de classificação baseado no corpo:
     - Use BodyPrefix/BodySuffix e/ou ContentsPrefix/HeaderLabel (fontes de /Contents).
     - NÃO use TopText/BottomText para evitar “porta giratória”.
     - Só aplique o fallback quando TitleKey/Title/TitleNormalized estiverem vazios.
     - Se não houver match forte, retorne "UNKNOWN" (nunca string vazia).
  2) Garanta que a evidência de detecção registre o método correto quando usar corpo (ex.: Method="body_prefix" ou
  "headerlabel").
  3) Adicione/ajuste teste unitário em tests/ObjPipeline.Tests para cobrir:
     - TitleKey vazio + BodyPrefix com “DESPACHO” => doc_type DESPACHO.
     - TitleKey vazio + sem corpo => doc_type UNKNOWN (não vazio).
  4) Rode os testes: dotnet test tests/ObjPipeline.Tests/ObjPipeline.Tests.csproj -v minimal

  Não altere outras regras de segmentação. Mantenha o foco em OBJ/Contents + alinhamento por operadores.
