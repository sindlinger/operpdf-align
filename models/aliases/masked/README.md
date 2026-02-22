# Modelos mascarados (debug)

Arquivos gerados para inspeção visual com texto sintético, sem nomes reais:

- `tjpb_despacho_masked_model.pdf`
- `tjpb_certidao_masked_model.pdf`
- `tjpb_requerimento_masked_model.pdf`

Os textos são renderizados com padrão mascarado (ex.: `PPPPP`, `0000`, `R$ 0000,00`, `CPF 000.000.000-00`).

## Uso direto (sem mexer em alias global)

```bash
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-despacho --inputs models/aliases/masked/tjpb_despacho_masked_model.pdf --inputs :Q22 --probe
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-certidao --inputs models/aliases/masked/tjpb_certidao_masked_model.pdf --inputs :Q22 --probe
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-requerimento --inputs models/aliases/masked/tjpb_requerimento_masked_model.pdf --inputs :Q22 --probe
```

## Uso por alias tipado (`@M-*`)

Para apontar os aliases tipados para modelos mascarados:

```bash
export OBJPDF_ALIAS_M_DES_DIR=/mnt/c/git/operpdf-textopsalign/models/aliases/masked/despacho
export OBJPDF_ALIAS_M_CER_DIR=/mnt/c/git/operpdf-textopsalign/models/aliases/masked/certidao
export OBJPDF_ALIAS_M_REQ_DIR=/mnt/c/git/operpdf-textopsalign/models/aliases/masked/requerimento
```

Depois:

```bash
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-despacho --inputs @M-DES --inputs :Q22 --probe
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-certidao --inputs @M-CER --inputs :Q22 --probe
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll textopsalign-requerimento --inputs @M-REQ --inputs :Q22 --probe
```

## Regenerar modelos mascarados

```bash
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll build-anchor-model-despacho --model models/aliases/despacho_merged/Documento_24_p090-091__merged_p1p2.pdf --out models/aliases/masked/tjpb_despacho_masked_model.pdf --render masked
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll build-anchor-model-despacho --model models/aliases/certidao/tjpb_certidao_conselho_model.pdf --out models/aliases/masked/tjpb_certidao_masked_model.pdf --render masked
dotnet cli/OperCli/bin/Release/net8.0/operpdf.dll build-anchor-model-despacho --model models/aliases/requerimento/tjpb_requerimento_model.pdf --out models/aliases/masked/tjpb_requerimento_masked_model.pdf --render masked
```
