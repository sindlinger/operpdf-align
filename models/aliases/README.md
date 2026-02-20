# Aliases de Modelo por Tipo

Este diretório separa modelos por tipo documental para uso via alias de CLI.

## Aliases

- `@M-DES`: carrega todos os PDFs em `models/aliases/despacho`
- `@M-CER`: carrega todos os PDFs em `models/aliases/certidao`
- `@M-REQ`: carrega todos os PDFs em `models/aliases/requerimento`

Quando um alias retorna múltiplos modelos e a execução tem um único alvo, o pipeline testa os candidatos do mesmo tipo e escolhe automaticamente o melhor modelo para o arquivo alvo.

## Regra operacional

- Misture somente modelos do mesmo tipo por execução.
- Para despacho, mantenha os PDFs de modelo em `models/aliases/despacho`.
- Para certidão, mantenha em `models/aliases/certidao`.
- Para requerimento, mantenha em `models/aliases/requerimento`.
