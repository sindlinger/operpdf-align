using System;
using System.Collections.Generic;
using System.Linq;

namespace Obj.ValidationCore
{
    public static class ValidationCatalog
    {
        public sealed class Rule
        {
            public int Number { get; init; }
            public string Id { get; init; } = "";
            public string Name { get; init; } = "";
            public string Scope { get; init; } = "";
            public string Module { get; init; } = "";
            public string Source { get; init; } = "";
        }

        private const string ScopeActiveTextOpsAlign = "active_textopsalign";
        private const string ScopeAvailable = "available";

        private static readonly IReadOnlyList<Rule> AllRules = BuildAllRules();

        private static readonly IReadOnlyList<string> SupportedFieldValidationKeys = new[]
        {
            "CPF_PERITO",
            "PROCESSO_JUDICIAL",
            "PROCESSO_ADMINISTRATIVO",
            "VALOR_ARBITRADO_JZ",
            "VALOR_ARBITRADO_DE",
            "VALOR_ARBITRADO_FINAL",
            "VALOR_ARBITRADO_CM",
            "VALOR_TABELADO_ANEXO_I",
            "DATA_ARBITRADO_FINAL",
            "DATA_AUTORIZACAO_CM",
            "DATA_REQUISICAO",
            "PERCENTUAL",
            "ADIANTAMENTO",
            "PARCELA",
            "PERITO",
            "PROMOVENTE",
            "PROMOVIDO",
            "ESPECIALIDADE",
            "ESPECIE_DA_PERICIA",
            "COMARCA",
            "VARA"
        };

        private static readonly IReadOnlyList<string> SupportedDocValidationProfiles = new[]
        {
            DocumentValidationRules.OutputDocDespacho,
            DocumentValidationRules.OutputDocCertidaoCm,
            DocumentValidationRules.OutputDocRequerimentoHonorarios
        };

        public static IReadOnlyList<Rule> GetAllRules()
        {
            return AllRules;
        }

        public static IReadOnlyList<Rule> GetActiveTextOpsAlignRules()
        {
            return AllRules
                .Where(r => string.Equals(r.Scope, ScopeActiveTextOpsAlign, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Number)
                .ToList();
        }

        public static IReadOnlyList<Rule> GetOtherAvailableRules()
        {
            return AllRules
                .Where(r => string.Equals(r.Scope, ScopeAvailable, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Number)
                .ToList();
        }

        public static IReadOnlyList<string> GetSupportedFieldValidationKeys()
        {
            return SupportedFieldValidationKeys;
        }

        public static IReadOnlyList<string> GetSupportedDocumentValidationProfiles()
        {
            return SupportedDocValidationProfiles;
        }

        public static string DescribeRulesInline(IReadOnlyList<Rule>? rules, int maxItems = 96)
        {
            if (rules == null || rules.Count == 0)
                return "(sem regras)";

            var ordered = rules
                .OrderBy(r => r.Number)
                .ToList();

            var take = Math.Max(1, maxItems);
            var items = ordered
                .Take(take)
                .Select(r => $"{r.Number}:{r.Id}:{r.Name}")
                .ToList();

            if (ordered.Count > take)
                items.Add($"+{ordered.Count - take}_regras");

            return string.Join(" | ", items);
        }

        private static IReadOnlyList<Rule> BuildAllRules()
        {
            return new List<Rule>
            {
                R(1, "VAL-CPF_PERITO-11_DIGITOS", "CPF do perito com 11 dígitos", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(2, "VAL-PROCESSO_JUDICIAL-FORMATO_CNJ", "Processo judicial em formato CNJ/legado", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(3, "VAL-PROCESSO_ADMINISTRATIVO-FORMATO_TJPB", "Processo administrativo em formatos TJPB", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(4, "VAL-VALOR_ARBITRADO-MONEY", "Campos VALOR_* no formato monetário", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(5, "VAL-DATAS-CAMPOS", "Campos DATA_* válidos", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(6, "VAL-PERCENTUAL-FORMATO", "Percentual válido", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(7, "VAL-ADIANTAMENTO-FORMATO", "Adiantamento válido (valor ou %)", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(8, "VAL-PARCELA-FORMATO", "Parcela válida (valor ou %)", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(9, "VAL-PERITO-FORMATO_BASE", "Validação base de nome de perito", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(10, "VAL-PARTES-FORMATO_BASE", "Validação base de partes", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(11, "VAL-ESPECIALIDADE-FORMATO_BASE", "Validação base de especialidade", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(12, "VAL-COMARCA-FORMATO_BASE", "Validação base de comarca", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(13, "VAL-VARA-FORMATO_BASE", "Validação base de vara", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValidFieldFormat"),
                R(14, "VAL-ANCHOR_LEAK", "Bloqueia vazamento de rótulo no valor", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(15, "VAL-CPF_EM_CAMPO_ERRADO", "Bloqueia CPF fora de CPF_PERITO", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(16, "VAL-PERITO_EM_CAMPO_ERRADO", "Bloqueia nome de perito em campo incorreto", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(17, "VAL-VARA_COMARCA_EM_CAMPO_ERRADO", "Bloqueia vara/comarca em campo incorreto", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(18, "VAL-INSTITUCIONAL_EM_NOME", "Bloqueia valor institucional em PERITO/PROMOVENTE/PROMOVIDO", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(19, "VAL-BOILERPLATE_EM_PARTES", "Bloqueia boilerplate em partes", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(20, "VAL-FORMAT_INVALID", "Reprovação por formato inválido", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:IsValueValidForField"),
                R(21, "DOC-REQ-OBRIGATORIOS", "Requerimento exige PROCESSO_ADMINISTRATIVO e DATA_REQUISICAO", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(22, "DOC-REQ-PROCESSO_JUDICIAL-IF_PRESENT", "Requerimento valida PROCESSO_JUDICIAL quando presente", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(23, "DOC-CERT-OBRIGATORIOS", "Certidão exige PROCESSO_ADMINISTRATIVO e (VALOR_ARBITRADO_CM|DATA_AUTORIZACAO_CM)", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(24, "DOC-CERT-PERITO-IF_PRESENT", "Certidão valida PERITO quando presente", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(25, "DOC-DESP-CORE_REQUIRED", "Despacho exige core documental", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(26, "DOC-DESP-PARTY_REPAIR", "Despacho tenta reparo de partes em OCR ruidoso", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(27, "DOC-STRICT-KNOWN-FIELDS", "Valida campos conhecidos não vazios", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ShouldRejectByValidator"),
                R(28, "PIPE-CERTIDAO-DATE_BRIDGE", "Bridge DATA_ARBITRADO_FINAL -> DATA_AUTORIZACAO_CM", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ApplyAndValidateDocumentValues"),
                R(29, "PIPE-OPTIONAL-ADIANTAMENTO", "ADIANTAMENTO tratado como opcional", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ApplyAndValidateDocumentValues"),
                R(30, "PIPE-FLOW-TRACE", "Fluxo explicável de validação", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorRules", "FieldValidationRules:ExplainDocumentValidationFlow"),
                R(31, "DETECT-SIGNATURE_METADATA_PAGE", "Bloqueia página de metadados de assinatura", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:LooksLikeSignatureMetadataPage"),
                R(32, "DETECT-DESPACHO-BLOCKED", "Bloqueios explícitos de despacho", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:IsBlockedDespacho"),
                R(33, "DETECT-CERTIDAO-GUARD", "Guarda documental de certidão", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:IsCertidaoConselho"),
                R(34, "DETECT-REQUERIMENTO-GUARD", "Guarda documental de requerimento", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:IsRequerimento"),
                R(35, "DETECT-REJECT_TEXT_MATCH", "Rejeição por texto proibido", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:HasRejectTextMatch"),
                R(36, "DETECT-OFICIO-LIKELY", "Heurística para evitar colisão com ofício", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:IsLikelyOficio"),
                R(37, "DETECT-CERTIDAO-PAGE_SIGNALS", "Sinais mínimos de página de certidão", ScopeActiveTextOpsAlign, "Obj.ValidationCore.DocumentValidationRules", "DocumentRuleSet:ValidateCertidaoPageSignals"),
                R(38, "DIAG-CPF_INVALID", "Diagnóstico de CPF inválido", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorDiagnostics", "ValidationDiagnostics:CollectSummaryIssues"),
                R(39, "DIAG-INSTITUTIONAL_PROMOVENTE", "Diagnóstico de promovente institucional", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorDiagnostics", "ValidationDiagnostics:CollectSummaryIssues"),
                R(40, "DIAG-INSTITUTIONAL_PROMOVIDO", "Diagnóstico de promovido institucional", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorDiagnostics", "ValidationDiagnostics:CollectSummaryIssues"),
                R(41, "DIAG-INSTITUTIONAL_PERITO", "Diagnóstico de perito institucional", ScopeActiveTextOpsAlign, "Obj.ValidationCore.ValidatorDiagnostics", "ValidationDiagnostics:CollectSummaryIssues"),
                R(42, "ANCHOR-TYPE_VALIDATION", "Validação por tipo na extração por âncora", ScopeAvailable, "AnchorTemplateExtractor.FieldValidators", "AnchorFieldValidators:IsValid"),
                R(43, "ANCHOR-SPAN_STRICT", "Tipos estritos por âncora exigem match forte", ScopeAvailable, "AnchorTemplateExtractor.AnchorExtractionEngine", "AnchorValidationEngine:TryAnchorSpanFallback"),
                R(44, "LEGACY-VALIDATOR_RULES_MIRROR", "Espelho legado das regras de validador", ScopeAvailable, "Obj.ValidatorModule.ValidatorRules", "modules/ValidatorModule/ValidatorRules.cs"),
                R(45, "LEGACY-DOC_RULESET_MIRROR", "Espelho legado das regras documentais", ScopeAvailable, "Obj.ValidatorModule.DocumentValidationRules", "modules/ValidatorModule/DocumentValidationRules.cs"),
                R(46, "LEGACY-DETECTION_POLICY_MIRROR", "Espelho legado da política de detecção", ScopeAvailable, "Obj.ValidatorModule.DocumentDetectionPolicy", "modules/ValidatorModule/DocumentDetectionPolicy.cs"),
                R(47, "LEGACY-DIAGNOSTICS_MIRROR", "Espelho legado de diagnósticos", ScopeAvailable, "Obj.ValidatorModule.ValidatorDiagnostics", "modules/ValidatorModule/ValidatorDiagnostics.cs"),
                R(48, "BRIDGE-EXTRACTOR-SUSPECT_CHECK", "Checagem de suspeitos no extractor legado", ScopeAvailable, "Obj.TjpbDespachoExtractor.Commands.TjpbDespachoExtractorCommand", "TjpbExtractorValidationBridge:IsSuspect")
            };
        }

        private static Rule R(
            int number,
            string id,
            string name,
            string scope,
            string module,
            string source)
        {
            return new Rule
            {
                Number = number,
                Id = id,
                Name = name,
                Scope = scope,
                Module = module,
                Source = source
            };
        }
    }
}
