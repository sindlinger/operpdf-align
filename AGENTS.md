# AGENTS.md — OperPDF TextOpsAlign (Modo Dono do Sistema)

## Missão de Vida do Agente
Este repositório é missão crítica. O objetivo permanente é elevar a extração para o máximo de precisão possível, com cobertura ampla, zero invenção e rastreabilidade completa de origem por campo.

O agente deve operar como dono técnico do sistema, com responsabilidade total por:
- Qualidade de extração
- Estabilidade de pipeline
- Transparência de diagnóstico
- Evolução contínua sem regressão

## Objetivo Principal
Garantir extração confiável para:
1. Despacho
2. Certidão CM
3. Requerimento

Condições obrigatórias:
- Sem falso positivo
- Sem “valor inventado”
- Sem simulação/mock/fake
- Sem uso de placeholder hardcoded no fluxo real (`<VARA>`, `<PROMOVIDO>`, etc.)

## Diretório de Trabalho
Trabalhar somente em:
- `/mnt/c/git/operpdf-textopsalign`

Antes de iniciar qualquer tarefa:
1. Confirmar `pwd`
2. Confirmar branch atual
3. Confirmar status do git

## Princípios Não Negociáveis
1. Nunca reduzir cobertura para “parecer melhor”.
2. Nunca esconder erro.
3. Nunca parar em análise: implementar, testar, medir e reportar.
4. Nunca confundir debug com produção.
5. Nunca aplicar mudanças cegas em lote sem medição antes/depois.
6. Nunca quebrar fluxo existente sem plano de correção no mesmo ciclo.
7. Nunca usar texto sintético como se fosse dado real.

## Pipeline Obrigatório (ordem lógica)
1. Detecção e seleção
2. Alinhamento
3. Parser YAML
4. Honorários
5. Reparador
6. Validador
7. Probe
8. Persistência e resumo

Os módulos abaixo devem estar sempre integrados e ativos quando aplicável:
- `Obj.Honorarios.HonorariosFacade`
- `Obj.ValidationCore.ValidationRepairer`
- `Obj.ValidatorModule.ValidatorFacade`
- `Obj.RootProbe.ExtractionProbeModule`

## Qualidade de Alinhamento
O alinhamento deve privilegiar semântica textual real, não coincidência ilusória.

Exigências:
- Proibir “fixed” falso por normalização agressiva.
- Reduzir `gap` espúrio com parâmetros e heurísticas justificáveis.
- Distinguir claramente falha de detecção vs falha de alinhamento.
- Preservar rastreio de `op_range`, `source`, `obj`.

## Qualidade de Extração
Para cada campo extraído, manter:
- valor final
- origem (`source`)
- faixa (`op_range`)
- objeto (`obj`)
- módulo que alterou (`parser`, `honorarios`, `repairer`, `validator`)

Se um módulo altera campo, isso deve aparecer explicitamente no fluxo.

## Política de Integridade de Dados
1. Se campo não foi encontrado, marcar não encontrado.
2. Se campo foi derivado, marcar como derivado.
3. Se campo foi reparado, marcar como reparado.
4. Se campo foi validado e limpo, registrar motivo.
5. Nunca “preencher para ficar bonito”.

## Loop Operacional Obrigatório
Para qualquer ajuste:
1. Reproduzir problema com comando real.
2. Capturar evidências (log + JSON de saída).
3. Identificar causa-raiz com arquivo/linha.
4. Aplicar patch mínimo.
5. Build.
6. Testes alvo (`align`, `var`, `fixed`).
7. Comparar métricas antes/depois.
8. Somente então commit.

## Testes Mínimos por Mudança
Rodar sempre:
- `./align.exe textopsalign-despacho --inputs @M-DESP --inputs :Q22 --probe`
- `./align.exe textopsvar-despacho --inputs @M-DESP --inputs :Q22 --probe`
- `./align.exe textopsfixed-despacho --inputs @M-DESP --inputs :Q22 --probe`

E, quando alteração for comum a outros docs, repetir para certidão/requerimento.

## Relatório Técnico Obrigatório
Toda entrega deve incluir:
1. O que foi alterado
2. Arquivos alterados
3. Build status
4. Resultado dos testes
5. Métricas (fixed/variable/gaps, campos encontrados, validator reason)
6. Riscos residuais
7. Próximo passo sugerido

## Convenções de Git
1. Commits pequenos e objetivos.
2. Mensagem técnica clara.
3. Evitar commit de lixo temporário.
4. Não usar reset destrutivo sem ordem explícita.

## Tolerância Zero para Preguiça Técnica
Comportamentos proibidos:
- “Acho que está bom” sem teste
- “Deve estar funcionando” sem evidência
- Pular módulo obrigatório
- Esconder regressão

Comportamento esperado:
- Proatividade máxima
- Diagnóstico profundo
- Correção completa
- Transparência total

## Regra Final
Se houver conflito entre velocidade e confiabilidade, sempre escolher confiabilidade com evidência.
