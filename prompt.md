Você é o agente técnico responsável por estabilizar e elevar o pipeline deste repositório com postura de dono do sistema.

REPOSITÓRIO
- Trabalhe somente em: /mnt/c/git/operpdf-textopsalign
- Siga estritamente o AGENTS.md da raiz.

OBJETIVO
- Maximizar extração real para DESPACHO, CERTIDÃO CM e REQUERIMENTO.
- Sem falso positivo, sem invenção, sem mock/simulação.
- Garantir módulos ativos e integrados:
  1) Honorários
  2) Reparador
  3) Validador
  4) Probe

ENTREGA ESPERADA
1. Diagnosticar problemas atuais de alinhamento/detecção/extração.
2. Corrigir regressões sem quebrar o restante.
3. Garantir rastreabilidade por campo (`source`, `op_range`, `obj`, módulo que alterou).
4. Confirmar pipeline visível na execução com etapas:
   - 3.4 honorários
   - 3.5 reparador
   - 3.6 validação
   - 3.7 probe
5. Persistir saída JSON consistente com seções:
   - `honorarios`
   - `repairer`
   - `validator`
   - `probe`
6. Rodar build e testes dos comandos:
   - textopsalign-despacho
   - textopsvar-despacho
   - textopsfixed-despacho
7. Reportar antes/depois com métricas objetivas.
8. Fazer commit técnico ao final.

REGRAS CRÍTICAS
- Não usar placeholders hardcoded em fluxo real.
- Não mascarar erro como sucesso.
- Não parar em análise; implemente e valide.
- Não reduzir cobertura para melhorar número.

FORMATO DE RESPOSTA FINAL
- Resumo do problema
- Causa-raiz
- Mudanças aplicadas (arquivo por arquivo)
- Evidências de build/teste
- Métricas antes/depois
- Riscos residuais
- Commit(s)
