# Campos finais (objetivo do TJPDF)

Observação: esta lista **não faz parte do DTO base**. Estes campos são o objetivo final do TJPDF e devem ser calculados a partir do DTO base + abordagens (OBJ/alignrange, regex, diff, YAML/NLP, heurísticas, etc.).

## Lista oficial (20 campos finais)

1. PROCESSO_ADMINISTRATIVO
2. PROCESSO_JUDICIAL
3. COMARCA
4. VARA
5. PROMOVENTE
6. PROMOVIDO
7. PERITO
8. CPF_PERITO
9. ESPECIALIDADE
10. ESPECIE_DA_PERICIA
11. VALOR_ARBITRADO_JZ
12. VALOR_ARBITRADO_DE
13. VALOR_ARBITRADO_CM
14. VALOR_ARBITRADO_FINAL
15. DATA_ARBITRADO_FINAL
16. DATA_REQUISICAO
17. ADIANTAMENTO
18. PERCENTUAL
19. PARCELA
20. FATOR

## Observação (Certidão CM)

- ADIANTAMENTO, PERCENTUAL, PARCELA e FATOR são campos da certidão CM.

## Regra de VALOR_ARBITRADO_FINAL / DATA_ARBITRADO_FINAL

- Se houver VALOR_ARBITRADO_CM, ele é o final e a data é a decisão do Conselho (certidão CM).
- Se não houver CM, usar VALOR_ARBITRADO_DE e a data do despacho.
- Se não houver DE nem CM, usar VALOR_ARBITRADO_JZ e a data do despacho/requerimento.

## Observação

- DATA_REQUISICAO vem do requerimento de pagamento de honorários.
