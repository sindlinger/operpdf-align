using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Obj.DocDetector;
using Obj.TjpbDespachoExtractor.Utils;

namespace Obj.ValidatorModule
{
    public static class DocumentValidationRules
    {
        public const string DocKeyDespacho = "despacho";
        public const string DocKeyCertidaoConselho = "certidao_conselho";
        public const string DocKeyRequerimentoHonorarios = "requerimento_honorarios";

        public const string OutputDocDespacho = "DESPACHO";
        public const string OutputDocCertidaoCm = "CERTIDAO_CM";
        public const string OutputDocRequerimentoHonorarios = "REQUERIMENTO_HONORARIOS";
        public const string ConsolidationInputDespacho = "despacho";
        public const string ConsolidationInputCertidao = "certidao";
        public const string ConsolidationInputRequerimento = "requerimento";

        private static readonly string[] CanonicalDocKeys =
        {
            DocKeyDespacho,
            DocKeyCertidaoConselho,
            DocKeyRequerimentoHonorarios
        };

        private static readonly string[] SignatureMetadataMarkers =
        {
            "documento assinado",
            "documento",
            "pagina",
            "assinado",
            "adme",
            "codigo de rastreabilidade",
            "consultadocumento",
            "pje.tjpb",
            "pje "
        };

        private static readonly string[] SignatureMetadataStrongHeaderMarkers =
        {
            "conselho da magistratura",
            "requerimento",
            "certidao",
            "honorarios",
            "honorários",
            "reserva orcamentaria",
            "reserva orçamentária",
            "requisicao",
            "requisição",
            "oficio",
            "ofício",
            "periciais"
        };

        public static string NormalizeDocText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var collapsed = TextUtils.CollapseSpacedLettersText(text);
            return TextUtils.NormalizeForMatch(collapsed);
        }

        private static bool ContainsNormalizedLoose(string normalizedHay, string normalizedNeedle)
        {
            if (string.IsNullOrWhiteSpace(normalizedHay) || string.IsNullOrWhiteSpace(normalizedNeedle))
                return false;
            if (normalizedHay.Contains(normalizedNeedle, StringComparison.Ordinal))
                return true;

            // Compact fallback only for multi-word phrases.
            // For single words this can create accidental cross-word matches.
            if (!normalizedNeedle.Contains(' ', StringComparison.Ordinal))
                return false;

            var hayCompact = normalizedHay.Replace(" ", "");
            var needleCompact = normalizedNeedle.Replace(" ", "");
            if (string.IsNullOrWhiteSpace(hayCompact) || string.IsNullOrWhiteSpace(needleCompact))
                return false;
            return hayCompact.Contains(needleCompact, StringComparison.Ordinal);
        }

        private static bool ContainsAnyNormalizedLoose(string normalizedHay, IEnumerable<string>? terms)
        {
            if (string.IsNullOrWhiteSpace(normalizedHay) || terms == null)
                return false;

            foreach (var term in terms)
            {
                var needle = NormalizeDocText(term);
                if (ContainsNormalizedLoose(normalizedHay, needle))
                    return true;
            }
            return false;
        }

        private static bool ContainsAllNormalizedLoose(string normalizedHay, IEnumerable<string>? terms)
        {
            if (string.IsNullOrWhiteSpace(normalizedHay) || terms == null)
                return false;

            foreach (var term in terms)
            {
                var needle = NormalizeDocText(term);
                if (string.IsNullOrWhiteSpace(needle))
                    continue;
                if (!ContainsNormalizedLoose(normalizedHay, needle))
                    return false;
            }
            return true;
        }

        public static bool ContainsAnyTerm(string? text, IEnumerable<string>? terms)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay) || terms == null)
                return false;

            foreach (var term in terms)
            {
                var needle = NormalizeDocText(term);
                if (ContainsNormalizedLoose(hay, needle))
                    return true;
            }

            return false;
        }

        public static bool HasSignatureMetadataStrongHeader(string? text)
        {
            return ContainsAnyTerm(text, SignatureMetadataStrongHeaderMarkers);
        }

        public static string NormalizeDocKey(string? docHint)
        {
            return ResolveDocKeyForDetection(docHint);
        }

        public static bool IsSupportedDocKey(string? docHint)
        {
            var key = ResolveDocKeyForDetection(docHint);
            return CanonicalDocKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        }

        public static string MapDocKeyToOutputType(string? docHint)
        {
            var key = ResolveDocKeyForDetection(docHint);
            if (string.Equals(key, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
                return OutputDocRequerimentoHonorarios;
            if (string.Equals(key, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
                return OutputDocCertidaoCm;
            if (string.Equals(key, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
                return OutputDocDespacho;
            return "";
        }

        public static string MapOutputTypeToDocKey(string? outputDocType)
        {
            if (string.IsNullOrWhiteSpace(outputDocType))
                return "";

            var key = outputDocType.Trim().ToUpperInvariant();
            if (string.Equals(key, OutputDocDespacho, StringComparison.OrdinalIgnoreCase))
                return DocKeyDespacho;
            if (string.Equals(key, OutputDocRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
                return DocKeyRequerimentoHonorarios;
            if (string.Equals(key, OutputDocCertidaoCm, StringComparison.OrdinalIgnoreCase))
                return DocKeyCertidaoConselho;
            return ResolveDocKeyForDetection(outputDocType);
        }

        public static string MapDocKeyToConsolidationInput(string? docHint)
        {
            var key = ResolveDocKeyForDetection(docHint);
            if (string.Equals(key, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
                return ConsolidationInputDespacho;
            if (string.Equals(key, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
                return ConsolidationInputRequerimento;
            if (string.Equals(key, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
                return ConsolidationInputCertidao;
            return "";
        }

        public static IReadOnlyList<string> GetDetectionKeywordsForDocExtended(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            var doc = DocDetectCatalog.GetDoc(key);
            var terms = new List<string>();

            terms.AddRange(GetDetectionKeywordsForDoc(key));
            terms.AddRange(GetBookmarkKeywordsForDoc(key));
            terms.AddRange(GetHeaderFallbackKeywordsForDoc(key));
            terms.AddRange(GetContentsTitleKeywordsForDoc(key));
            terms.AddRange(doc.StrongAny ?? new List<string>());

            if (doc.StrongPairs != null)
            {
                foreach (var pair in doc.StrongPairs)
                {
                    if (pair == null) continue;
                    terms.AddRange(pair);
                }
            }

            if (doc.WeakPairs != null)
            {
                foreach (var pair in doc.WeakPairs)
                {
                    if (pair == null) continue;
                    terms.AddRange(pair);
                }
            }

            return terms
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetDetectionKeywordsAll(
            bool includeGenericHeaders = true,
            bool includeExtendedSignals = true)
        {
            var terms = new List<string>();
            foreach (var key in CanonicalDocKeys)
            {
                if (includeExtendedSignals)
                    terms.AddRange(GetDetectionKeywordsForDocExtended(key));
                else
                    terms.AddRange(GetDetectionKeywordsForDoc(key));
            }

            if (includeGenericHeaders)
                terms.AddRange(GetGenericHeaderLabels());

            return terms
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static DetectionOptions BuildDefaultDetectionOptions(IEnumerable<string>? keywords = null)
        {
            var kw = (keywords ?? GetDetectionKeywordsAll())
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DetectionOptions
            {
                PrefixOpCount = 400,
                SuffixOpCount = 120,
                CarryForward = true,
                TopBandPct = 0.45,
                BottomBandPct = 0.25,
                UseTopTextFallback = false,
                Keywords = kw
            };
        }

        public static bool MatchGuard(string? text, GuardRules? guard)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;

            var hasRules = (guard?.Require?.Count ?? 0) > 0 || (guard?.Any?.Count ?? 0) > 0;
            if (!hasRules)
                return false;

            if (guard?.Require != null && guard.Require.Count > 0 && !ContainsAllNormalizedLoose(hay, guard.Require))
                return false;
            if (guard?.Any != null && guard.Any.Count > 0 && !ContainsAnyNormalizedLoose(hay, guard.Any))
                return false;
            return true;
        }

        public static bool ContainsAnyGroup(string? text, List<List<string>>? groups)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay) || groups == null || groups.Count == 0)
                return false;

            foreach (var group in groups)
            {
                if (group == null || group.Count == 0)
                    continue;
                if (ContainsAllNormalizedLoose(hay, group))
                    return true;
            }

            return false;
        }

        public static bool HasStrongSignals(DocDetectDoc? doc, string? text)
        {
            if (doc == null)
                return false;

            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;

            var strongAny = doc.StrongAny ?? new List<string>();
            if (ContainsAnyNormalizedLoose(hay, strongAny))
                return true;
            if (ContainsAnyGroup(hay, doc.StrongPairs))
                return true;
            return false;
        }

        public static bool LooksLikeSignatureMetadataPage(string? text)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return true;

            var hits = 0;
            foreach (var marker in SignatureMetadataMarkers)
            {
                if (hay.Contains(marker, StringComparison.Ordinal))
                    hits++;
            }

            var hasStrongHeader = HasSignatureMetadataStrongHeader(hay);

            return hits >= 3 && !hasStrongHeader;
        }

        public static bool IsCertidaoConselho(string? text, bool rejectSignatureMetadata = false)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyCertidaoConselho);
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            if (!MatchGuard(hay, doc.Guard))
                return false;
            if (rejectSignatureMetadata && LooksLikeSignatureMetadataPage(hay))
                return false;

            var hasStrongConfig = (doc.StrongAny?.Count ?? 0) > 0 || (doc.StrongPairs?.Count ?? 0) > 0;
            if (!hasStrongConfig)
                return true;
            return HasStrongSignals(doc, hay);
        }

        public static bool IsCertidaoConselhoFromTopBody(string? top, string? body)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyCertidaoConselho);
            var topNorm = NormalizeDocText(top);
            var bodyNorm = NormalizeDocText(body);
            var hay = NormalizeDocText($"{topNorm} {bodyNorm}");
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            if (!MatchGuard(hay, doc.Guard))
                return false;

            var hasTitle = ContainsAnyNormalizedLoose(topNorm, doc.TitleKeywords) ||
                           ContainsAnyNormalizedLoose(bodyNorm, doc.TitleKeywords);
            if (!hasTitle)
                return false;
            if (LooksLikeSignatureMetadataPage(hay))
                return false;

            var hasStrongConfig = (doc.StrongAny?.Count ?? 0) > 0 || (doc.StrongPairs?.Count ?? 0) > 0;
            if (!hasStrongConfig)
                return true;
            return HasStrongSignals(doc, hay);
        }

        public static bool IsRequerimento(string? text)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyRequerimentoHonorarios);
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            if (MatchGuard(hay, doc.Guard))
                return true;
            if (ContainsAnyGroup(hay, doc.WeakPairs))
                return true;
            return false;
        }

        public static bool IsRequerimentoStrong(string? text)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyRequerimentoHonorarios);
            return HasStrongSignals(doc, text);
        }

        public static bool IsRequerimentoFromTopBody(string? top, string? body)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyRequerimentoHonorarios);
            var topNorm = NormalizeDocText(top);
            var bodyNorm = NormalizeDocText(body);
            var hay = NormalizeDocText($"{topNorm} {bodyNorm}");
            if (string.IsNullOrWhiteSpace(hay))
                return false;

            var hasRequerimentoSemantics = ContainsAnyNormalizedLoose(hay, new[]
            {
                "requisicao de pagamento",
                "requisicao de pagamento de honorarios periciais",
                "solicita pagamento",
                "pagamento de honorarios",
                "pagamento da pericia",
                "relativo ao pagamento da pericia",
                "pedido de providencias",
                "venho requerer",
                "honorarios periciais"
            });
            var guardOrWeak = MatchGuard(hay, doc.Guard) || ContainsAnyGroup(hay, doc.WeakPairs);
            var hasStrong = HasStrongSignals(doc, hay);
            var hasExplicitRequerimentoMarker = ContainsAnyNormalizedLoose(hay, new[]
            {
                "requerimento",
                "venho requerer",
                "pedido de providencias",
                "pedido de providências"
            });
            if (!guardOrWeak)
            {
                if (!hasStrong && !hasRequerimentoSemantics)
                    return false;
                if (!hasExplicitRequerimentoMarker && !hasRequerimentoSemantics)
                    return false;
            }

            var hasTitle = ContainsAnyNormalizedLoose(topNorm, doc.TitleKeywords) ||
                           ContainsAnyNormalizedLoose(bodyNorm, doc.TitleKeywords);
            if (!hasTitle && !hasExplicitRequerimentoMarker && !hasRequerimentoSemantics && !hasStrong)
                return false;
            return hasTitle || hasStrong || hasRequerimentoSemantics;
        }

        public static bool IsBlockedDespacho(string? text)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;

            var doc = DocDetectCatalog.GetDoc(DocKeyDespacho);
            if (doc.BlockedAny != null)
            {
                foreach (var term in doc.BlockedAny)
                {
                    var needle = NormalizeDocText(term);
                    if (!string.IsNullOrWhiteSpace(needle) && hay.Contains(needle, StringComparison.Ordinal))
                        return true;
                }
            }

            if (ContainsAnyGroup(hay, doc.BlockedAll))
                return true;
            return false;
        }

        public static bool IsDespacho(string? text)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            if (IsBlockedDespacho(hay))
                return false;
            var keywords = DocDetectCatalog.GetTitleKeywords(DocKeyDespacho);
            return DocDetectCatalog.ContainsAnyNormalized(hay, keywords);
        }

        public static string ResolveDocKeyFromHint(string? hint)
        {
            var norm = NormalizeDocText(hint);
            if (string.IsNullOrWhiteSpace(norm))
                return "";

            // CLI-friendly short aliases
            if (norm.Contains("desp", StringComparison.Ordinal))
                return DocKeyDespacho;
            if (norm.Contains("cert", StringComparison.Ordinal))
                return DocKeyCertidaoConselho;
            if (norm.Contains("requer", StringComparison.Ordinal) || norm == "req")
                return DocKeyRequerimentoHonorarios;

            var rules = DocDetectCatalog.Load();
            foreach (var key in rules.Docs.Keys)
            {
                if (IsTokenForDoc(key, norm))
                    return key;
            }

            return "";
        }

        public static bool IsDocMatch(string? candidateDoc, string? filterDoc)
        {
            var filter = ResolveDocKeyFromHint(filterDoc);
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            var candidate = ResolveDocKeyFromHint(candidateDoc);
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            return string.Equals(candidate, filter, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeOnlyDocFilter(string? onlyDoc)
        {
            return ResolveDocKeyFromHint(onlyDoc);
        }

        public static string ResolvePatternForDoc(string? docType)
        {
            var key = ResolveDocKeyFromHint(docType);
            if (string.Equals(key, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
                return "tjpb_certidao_cm";
            if (string.Equals(key, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
                return "tjpb_requerimento";
            return "tjpb_despacho";
        }

        public static string ResolveDocKeyFromPatternPath(string? patternsPath, string? docNameHint = null)
        {
            var fromHint = ResolveDocKeyFromHint(docNameHint);
            if (!string.IsNullOrWhiteSpace(fromHint))
                return fromHint;

            if (string.IsNullOrWhiteSpace(patternsPath))
                return "";

            try
            {
                if (File.Exists(patternsPath) && Path.GetExtension(patternsPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(patternsPath));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (!string.Equals(prop.Name, "doc", StringComparison.OrdinalIgnoreCase))
                                continue;
                            if (prop.Value.ValueKind != JsonValueKind.String)
                                continue;
                            var fromJson = ResolveDocKeyFromHint(prop.Value.GetString());
                            if (!string.IsNullOrWhiteSpace(fromJson))
                                return fromJson;
                        }
                    }
                }
            }
            catch
            {
                // fallback to file name routing
            }

            var fileName = Path.GetFileNameWithoutExtension(patternsPath);
            return ResolveDocKeyFromHint(fileName);
        }

        public static string ResolveHonorariosDocTypeFromPatternPath(string? patternsPath, string? docNameHint = null)
        {
            var key = ResolveDocKeyFromPatternPath(patternsPath, docNameHint);
            var mapped = MapDocKeyToOutputType(key);
            return string.IsNullOrWhiteSpace(mapped) ? OutputDocDespacho : mapped;
        }

        public static bool IsBookmarkTitleForDoc(string? title, string docKey)
        {
            if (string.IsNullOrWhiteSpace(docKey))
                return false;
            var norm = NormalizeDocText(title);
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            var refs = BuildDocReferenceTokens(docKey);
            return refs.Count > 0 && DocDetectCatalog.ContainsAnyNormalized(norm, refs);
        }

        public static bool IsCertidaoBookmarkTitle(string? title)
        {
            return IsBookmarkTitleForDoc(title, DocKeyCertidaoConselho);
        }

        public static IReadOnlyList<string> GetCombinedTitleKeywords()
        {
            return DocDetectCatalog.GetCombinedTitleKeywords();
        }

        public static IReadOnlyList<string> GetTitleKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            return DocDetectCatalog.GetTitleKeywords(key);
        }

        public static IReadOnlyList<string> GetContentsTitleKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            return DocDetectCatalog.GetContentsTitleKeywords(key);
        }

        public static IReadOnlyList<string> GetHeaderFallbackKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            return DocDetectCatalog.GetHeaderFallbacks(key)
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetBookmarkKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            return DocDetectCatalog.GetBookmarkKeywords(key)
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetGuardRequireKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            var guard = DocDetectCatalog.GetGuard(key);
            return (guard.Require ?? new List<string>())
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetGuardAnyKeywordsForDoc(string? docHint)
        {
            var key = ResolveDocKeyForLookup(docHint);
            var guard = DocDetectCatalog.GetGuard(key);
            return (guard.Any ?? new List<string>())
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetDetectionKeywordsForDoc(string? docHint)
        {
            return GetTitleKeywordsForDoc(docHint)
                .Concat(GetGuardRequireKeywordsForDoc(docHint))
                .Concat(GetGuardAnyKeywordsForDoc(docHint))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> GetGenericHeaderLabels()
        {
            return DocDetectCatalog.GetGenericHeaderLabels()
                .Select(NormalizeDocText)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static string ResolveDocKeyForDetection(string? docHint)
        {
            return ResolveDocKeyForLookup(docHint);
        }

        public static bool ContainsTitleKeywordsForDoc(string? text, string? docHint)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            return DocDetectCatalog.ContainsAnyNormalized(hay, GetTitleKeywordsForDoc(docHint));
        }

        public static bool ContainsContentsTitleKeywordsForDoc(string? text, string? docHint)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            return DocDetectCatalog.ContainsAnyNormalized(hay, GetContentsTitleKeywordsForDoc(docHint));
        }

        public static bool ContainsHeaderFallbackForDoc(string? text, string? docHint)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            return DocDetectCatalog.ContainsAnyNormalized(hay, GetHeaderFallbackKeywordsForDoc(docHint));
        }

        public static bool ContainsBookmarkKeywordsForDoc(string? text, string? docHint)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            return DocDetectCatalog.ContainsAnyNormalized(hay, GetBookmarkKeywordsForDoc(docHint));
        }

        public static bool IsGenericHeaderLabel(string? text)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay))
                return false;
            return DocDetectCatalog.ContainsAnyNormalized(hay, GetGenericHeaderLabels());
        }

        public static string FindMatchedKeyword(string? text, IEnumerable<string>? keywords)
        {
            var hay = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(hay) || keywords == null)
                return "";

            foreach (var raw in keywords)
            {
                var needle = NormalizeDocText(raw);
                if (string.IsNullOrWhiteSpace(needle))
                    continue;
                if (ContainsNormalizedLoose(hay, needle))
                    return needle;
            }

            return "";
        }

        public static bool ShouldFallbackDetectDoc(
            bool strictDocValidation,
            double weightedScore,
            bool detectedByModel,
            string? blockReason,
            out string reason)
        {
            reason = "";
            if (!strictDocValidation)
                return false;

            if (detectedByModel && weightedScore >= 5.0)
                return false;

            reason = !detectedByModel
                ? (string.IsNullOrWhiteSpace(blockReason) ? "detectdoc_not_found" : $"detectdoc_{blockReason}")
                : $"detectdoc_low_score:{weightedScore:0.00}";
            return true;
        }

        public static List<string> GetMissingRequiredFields(
            bool strictDocValidation,
            bool requireAll,
            IEnumerable<string>? requiredFields,
            IDictionary<string, string>? values)
        {
            var missing = new List<string>();
            if (!strictDocValidation || !requireAll || requiredFields == null || values == null)
                return missing;

            foreach (var field in requiredFields.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryGetValueIgnoreCase(values, field, out var value) || string.IsNullOrWhiteSpace(value))
                    missing.Add(field);
            }
            return missing;
        }

        public static bool HasRejectTextMatch(string? text, IEnumerable<string>? rejectTexts)
        {
            if (string.IsNullOrWhiteSpace(text) || rejectTexts == null)
                return false;

            var norm = NormalizeDocText(text);
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            var normNoSpace = norm.Replace(" ", "");
            foreach (var phrase in rejectTexts)
            {
                var phraseNorm = NormalizeDocText(phrase);
                if (string.IsNullOrWhiteSpace(phraseNorm))
                    continue;

                if (norm.Contains(phraseNorm, StringComparison.Ordinal))
                    return true;

                var phraseNoSpace = phraseNorm.Replace(" ", "");
                if (!string.IsNullOrWhiteSpace(phraseNoSpace) &&
                    normNoSpace.Contains(phraseNoSpace, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static bool HasRejectTextMatchLoose(string? text, IEnumerable<string>? rejectTexts)
        {
            if (HasRejectTextMatch(text, rejectTexts))
                return true;
            if (string.IsNullOrWhiteSpace(text) || rejectTexts == null)
                return false;

            var normLoose = TextUtils.NormalizeForMatch(text);
            if (string.IsNullOrWhiteSpace(normLoose))
                return false;

            foreach (var phrase in rejectTexts)
            {
                var phraseLoose = TextUtils.NormalizeForMatch(phrase);
                if (!string.IsNullOrWhiteSpace(phraseLoose) &&
                    normLoose.Contains(phraseLoose, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        public static void EnsureDespachoRejectTexts(ISet<string>? rejectTexts, string? docName)
        {
            if (rejectTexts == null)
                return;

            var docKey = DocDetectCatalog.ResolveDocKey(docName);
            var defaultKey = DocDetectCatalog.ResolveDocKey(null);
            if (!string.Equals(docKey, defaultKey, StringComparison.OrdinalIgnoreCase))
                return;

            var oficio = DocDetectCatalog.GetOficioRules();
            foreach (var item in oficio.Markers)
                rejectTexts.Add(item);
            foreach (var item in oficio.Phrases)
                rejectTexts.Add(item);
            foreach (var item in oficio.Compact)
                rejectTexts.Add(item);
        }

        public static bool IsLikelyOficioRaw(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var rules = DocDetectCatalog.GetOficioRules();
            var lower = text.ToLowerInvariant();
            var markers = rules.Markers.Select(m => (m ?? "").ToLowerInvariant()).ToList();
            var context = rules.Context.Select(m => (m ?? "").ToLowerInvariant()).ToList();
            var compactHints = rules.Compact.Select(m => (m ?? "").ToLowerInvariant()).ToList();

            var hasMarker = ContainsAnyLower(lower, markers);
            var hasContext = ContainsAnyLower(lower, context);
            if (hasMarker && hasContext)
                return true;

            var compact = lower.Replace(" ", "");
            var rawCompact = new string(lower.Where(char.IsLetterOrDigit).ToArray());

            if (ContainsAnyLower(compact, compactHints) || ContainsAnyLower(rawCompact, compactHints))
                return true;
            if (ContainsAnyLower(compact, markers) && ContainsAnyLower(compact, context))
                return true;
            if (ContainsAnyLower(rawCompact, markers) && ContainsAnyLower(rawCompact, context))
                return true;

            return false;
        }

        public static bool IsLikelyOficio(string? norm)
        {
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            var rules = DocDetectCatalog.GetOficioRules();
            var compact = norm.Replace(" ", "");
            var hasMarker = DocDetectCatalog.ContainsAnyNormalized(norm, rules.Markers);
            var hasContext = DocDetectCatalog.ContainsAnyNormalized(norm, rules.Context);

            if (hasMarker && hasContext)
                return true;
            if (DocDetectCatalog.ContainsAnyNormalized(norm, rules.Phrases))
                return true;
            if (DocDetectCatalog.ContainsAnyNormalized(compact, rules.Compact))
                return true;
            if (DocDetectCatalog.ContainsAnyNormalized(compact, rules.Markers) &&
                DocDetectCatalog.ContainsAnyNormalized(compact, rules.Context))
                return true;

            return false;
        }

        public static bool IsLikelyOficioLoose(string? norm, string? compact)
        {
            if (string.IsNullOrWhiteSpace(norm))
                return false;
            if (IsLikelyOficio(norm))
                return true;
            if (string.IsNullOrWhiteSpace(compact))
                return false;
            return IsLikelyOficio(compact);
        }

        public static bool ValidateCertidaoPageSignals(
            bool hasHeader,
            bool hasTitle,
            bool hasBody,
            bool hasMoney,
            bool hasRobsonFooter,
            out string reason)
        {
            if (!hasHeader)
            {
                reason = "missing_header_hint";
                return false;
            }
            if (!hasTitle)
            {
                reason = "missing_title_hint";
                return false;
            }
            if (!hasBody && !hasMoney)
            {
                reason = "missing_body_or_money";
                return false;
            }
            if (!hasRobsonFooter)
            {
                reason = "missing_robson_footer";
                return false;
            }

            reason = "ok";
            return true;
        }

        public static bool IsCertidaoLooseGuard(string? top, string? body)
        {
            var doc = DocDetectCatalog.GetDoc(DocKeyCertidaoConselho);
            var topNorm = NormalizeDocText(top);
            var bodyNorm = NormalizeDocText(body);
            var hay = NormalizeDocText($"{topNorm} {bodyNorm}");
            if (string.IsNullOrWhiteSpace(hay))
                return false;

            if (LooksLikeSignatureMetadataPage(hay))
                return false;

            var hasTitle = ContainsAnyNormalizedLoose(topNorm, doc.TitleKeywords) ||
                           ContainsAnyNormalizedLoose(bodyNorm, doc.TitleKeywords);
            if (!hasTitle)
                return false;

            var hasAny = doc.Guard?.Any != null &&
                         doc.Guard.Any.Count > 0 &&
                         ContainsAnyNormalizedLoose(hay, doc.Guard.Any);
            if (!hasAny)
                return false;

            var hasDocSpecificSignal = DocDetectCatalog.ContainsAnyNormalized(hay, doc.StrongAny ?? new List<string>());
            if (!hasDocSpecificSignal)
                return false;

            return hay.Contains("processo", StringComparison.Ordinal) ||
                   hay.Contains("autos", StringComparison.Ordinal);
        }

        public static bool IsTargetGuardPass(string targetDocKey, string? top, string? body)
        {
            if (string.Equals(targetDocKey, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
            {
                if (IsCertidaoConselhoFromTopBody(top, body))
                    return true;
                return IsCertidaoLooseGuard(top, body);
            }
            if (string.Equals(targetDocKey, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
                return IsRequerimentoFromTopBody(top, body);
            if (string.Equals(targetDocKey, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
            {
                var combined = $"{top} {body}";
                if (LooksLikeSignatureMetadataPage(combined))
                    return false;
                return IsDespacho(combined);
            }
            return false;
        }

        public static bool IsTargetStrongPass(string targetDocKey, string? combined)
        {
            if (string.Equals(targetDocKey, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
            {
                if (IsCertidaoConselho(combined, rejectSignatureMetadata: true))
                    return true;
                return IsCertidaoLooseGuard("", combined);
            }
            if (string.Equals(targetDocKey, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
            {
                if (IsRequerimentoFromTopBody("", combined))
                    return true;

                return IsRequerimentoStrong(combined) || IsRequerimento(combined);
            }
            if (string.Equals(targetDocKey, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
            {
                if (LooksLikeSignatureMetadataPage(combined))
                    return false;
                return IsDespacho(combined);
            }
            return false;
        }

        public static bool IsOtherDocStrongAgainstTarget(
            string targetDocKey,
            string? combined,
            string? top,
            string? body)
        {
            if (string.Equals(targetDocKey, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
            {
                if (IsRequerimentoFromTopBody(top, body))
                    return true;
                return IsDespacho(combined);
            }

            if (string.Equals(targetDocKey, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
            {
                if (IsCertidaoConselhoFromTopBody(top, body))
                    return true;
                return IsDespacho(combined);
            }

            if (string.Equals(targetDocKey, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
            {
                if (IsCertidaoConselhoFromTopBody(top, body))
                    return true;
                if (IsRequerimentoFromTopBody(top, body))
                    return true;
                return false;
            }

            return false;
        }

        public static List<string> BuildDocReferenceTokens(string docKey)
        {
            var resolved = DocDetectCatalog.ResolveDocKey(docKey);
            if (string.IsNullOrWhiteSpace(resolved))
                return new List<string>();

            var doc = DocDetectCatalog.GetDoc(resolved);
            var refs = new List<string>
            {
                resolved
            };
            refs.AddRange(doc.Aliases ?? new List<string>());
            refs.AddRange(doc.TitleKeywords ?? new List<string>());
            refs.AddRange(doc.BookmarkKeywords ?? new List<string>());
            refs.AddRange(doc.HeaderFallbacks ?? new List<string>());
            refs.AddRange(doc.ContentsTitleKeywords ?? new List<string>());
            return refs;
        }

        public static bool IsTokenForDoc(string docKey, string? token)
        {
            if (string.IsNullOrWhiteSpace(docKey) || string.IsNullOrWhiteSpace(token))
                return false;

            var norm = NormalizeDocText(token);
            if (string.IsNullOrWhiteSpace(norm))
                return false;

            var refs = BuildDocReferenceTokens(docKey);
            return refs.Count > 0 && DocDetectCatalog.ContainsAnyNormalized(norm, refs);
        }

        public static bool IsTokenForOtherDoc(string targetDocKey, string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return false;

            var rules = DocDetectCatalog.Load();
            foreach (var key in rules.Docs.Keys)
            {
                if (string.Equals(key, targetDocKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsTokenForDoc(key, token))
                    return true;
            }
            return false;
        }

        public static (double MatchScore, string MatchedDocKey, string Notes) EvaluateWeightedDocSupport(
            string targetDocKey,
            string detector,
            string? evidence,
            string? titleKey,
            string? matchedKeyword)
        {
            var norm = NormalizeDocText(evidence);
            var isDespacho = IsDespacho(norm);
            var isCertidao = IsCertidaoConselho(norm, rejectSignatureMetadata: true);
            var isRequerimento = IsRequerimento(norm);
            var isRequerimentoStrong = IsRequerimentoStrong(norm);
            var blockedDespacho = IsBlockedDespacho(norm);

            var matchedDoc = "";
            if (isCertidao)
                matchedDoc = DocKeyCertidaoConselho;
            else if (isRequerimento)
                matchedDoc = DocKeyRequerimentoHonorarios;
            else if (isDespacho)
                matchedDoc = DocKeyDespacho;

            var aliasForTarget = IsTokenForDoc(targetDocKey, titleKey) || IsTokenForDoc(targetDocKey, matchedKeyword);
            var aliasForOther = IsTokenForOtherDoc(targetDocKey, titleKey) || IsTokenForOtherDoc(targetDocKey, matchedKeyword);

            var match = 0.0;
            if (string.Equals(targetDocKey, DocKeyDespacho, StringComparison.OrdinalIgnoreCase))
            {
                if (blockedDespacho)
                    match = 0.0;
                else if (isDespacho)
                    match = 1.0;
                else if (aliasForTarget)
                    match = 0.35;
                else if (detector.IndexOf("LargestContentsDetector", StringComparison.OrdinalIgnoreCase) >= 0)
                    match = 0.15;
            }
            else if (string.Equals(targetDocKey, DocKeyCertidaoConselho, StringComparison.OrdinalIgnoreCase))
            {
                if (isCertidao)
                    match = 1.0;
                else if (aliasForTarget)
                    match = 0.40;
                else if (detector.IndexOf("LargestContentsDetector", StringComparison.OrdinalIgnoreCase) >= 0)
                    match = 0.05;
            }
            else if (string.Equals(targetDocKey, DocKeyRequerimentoHonorarios, StringComparison.OrdinalIgnoreCase))
            {
                if (isRequerimento)
                    match = isRequerimentoStrong ? 1.0 : 0.90;
                else if (aliasForTarget)
                    match = 0.40;
                else if (detector.IndexOf("LargestContentsDetector", StringComparison.OrdinalIgnoreCase) >= 0)
                    match = 0.05;
            }

            if (detector.IndexOf("NonDespachoDetector.target", StringComparison.OrdinalIgnoreCase) >= 0 &&
                string.Equals(titleKey, targetDocKey, StringComparison.OrdinalIgnoreCase))
            {
                match = Math.Max(match, 1.0);
            }

            if (aliasForOther && !aliasForTarget)
                match *= 0.25;

            if (match < 0) match = 0;
            if (match > 1) match = 1;

            var notes = $"desp={isDespacho} cert={isCertidao} req={isRequerimento} blocked={blockedDespacho} alias_target={aliasForTarget} alias_other={aliasForOther}";
            return (match, matchedDoc, notes);
        }

        public static double ComputeNonDespachoWeightedMatch(
            bool guardPass,
            bool strongPass,
            bool hasTitleKeyword,
            bool otherDocStrong,
            bool aliasTarget,
            bool aliasOther,
            bool isLargestContentsDetector)
        {
            var match = 0.0;
            if (guardPass)
                match = 1.0;
            else if (strongPass && hasTitleKeyword)
                match = 0.85;
            else if (strongPass)
                match = 0.60;
            else if (hasTitleKeyword || aliasTarget)
                match = isLargestContentsDetector ? 0.20 : 0.45;
            else if (isLargestContentsDetector)
                match = 0.05;

            if (otherDocStrong && !guardPass)
                match *= 0.20;
            if (aliasOther && !aliasTarget)
                match *= 0.40;

            if (match < 0) match = 0;
            if (match > 1) match = 1;
            return match;
        }

        public static bool ShouldAcceptNonDespachoWinner(
            bool guardPass,
            bool strongPass,
            bool hasTitleKeyword,
            double candidateScore,
            double minStrongTitleScore = 6.0)
        {
            if (guardPass)
                return true;
            return strongPass && hasTitleKeyword && candidateScore >= minStrongTitleScore;
        }

        public static string ClassifyDocByPageEvidence(
            string? key,
            string? title,
            string? normalizedTitle,
            string? body,
            string? combined,
            out string method)
        {
            method = "";
            var keyNorm = NormalizeDocText(key);
            var titleNorm = NormalizeDocText(title);
            var normalized = NormalizeDocText(normalizedTitle);
            var bodyNorm = NormalizeDocText(body);
            var combinedNorm = NormalizeDocText(combined);
            var blocked = IsBlockedDespacho(bodyNorm) || IsBlockedDespacho(combinedNorm);

            if (IsCertidaoConselho(keyNorm) || IsCertidaoConselho(titleNorm) || IsCertidaoConselho(normalized))
            {
                method = "title_top_bottom";
                return DocKeyCertidaoConselho;
            }
            if (IsRequerimentoStrong(keyNorm) || IsRequerimentoStrong(titleNorm) || IsRequerimentoStrong(normalized))
            {
                method = "title_top_bottom";
                return DocKeyRequerimentoHonorarios;
            }
            if (!blocked && (IsDespacho(keyNorm) || IsDespacho(titleNorm) || IsDespacho(normalized)))
            {
                method = "title_top_bottom";
                return DocKeyDespacho;
            }
            if (IsRequerimento(keyNorm) || IsRequerimento(titleNorm) || IsRequerimento(normalized))
            {
                method = "title_top_bottom";
                return DocKeyRequerimentoHonorarios;
            }

            if (IsCertidaoConselho(bodyNorm))
            {
                method = "body_prefix";
                return DocKeyCertidaoConselho;
            }
            if (IsRequerimentoStrong(bodyNorm))
            {
                method = "body_prefix";
                return DocKeyRequerimentoHonorarios;
            }
            if (!blocked && IsDespacho(bodyNorm))
            {
                method = "body_prefix";
                return DocKeyDespacho;
            }
            if (IsRequerimento(bodyNorm))
            {
                method = "body_prefix";
                return DocKeyRequerimentoHonorarios;
            }

            return "UNKNOWN";
        }

        public static int ScoreDocumentCandidate(
            string docType,
            bool hasBackBodyObj,
            string? top,
            string? body,
            string? combined)
        {
            var topNorm = NormalizeDocText(top);
            var bodyNorm = NormalizeDocText(body);
            var combinedNorm = NormalizeDocText(combined);

            int score = 0;
            if (docType == DocKeyDespacho)
            {
                if (IsDespacho(topNorm)) score += 5;
                if (IsDespacho(bodyNorm)) score += 3;
                if (IsDespacho(combinedNorm)) score += 1;
                if (!hasBackBodyObj) score -= 5;
            }
            else if (docType == DocKeyRequerimentoHonorarios)
            {
                if (IsRequerimentoStrong(combinedNorm)) score += 5;
                else if (IsRequerimento(combinedNorm)) score += 3;

                if (combinedNorm.Contains("processo", StringComparison.Ordinal) ||
                    combinedNorm.Contains("comarca", StringComparison.Ordinal) ||
                    combinedNorm.Contains("vara", StringComparison.Ordinal) ||
                    combinedNorm.Contains("juizo", StringComparison.Ordinal))
                    score += 1;
            }
            else if (docType == DocKeyCertidaoConselho)
            {
                if (IsCertidaoConselho(combinedNorm)) score += 5;
                if (DocDetectCatalog.ContainsAnyNormalized(combinedNorm, DocDetectCatalog.GetTitleKeywords(DocKeyCertidaoConselho)))
                    score += 1;
                if (combinedNorm.Contains("sala de sessoes", StringComparison.Ordinal))
                    score += 2;
            }

            return Math.Max(0, score);
        }

        private static bool TryGetValueIgnoreCase(IDictionary<string, string> values, string field, out string value)
        {
            if (values.TryGetValue(field, out value!))
                return true;

            foreach (var kv in values)
            {
                if (string.Equals(kv.Key, field, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }

            value = "";
            return false;
        }

        private static string ResolveDocKeyForLookup(string? docHint)
        {
            var resolved = ResolveDocKeyFromHint(docHint);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
            return DocDetectCatalog.ResolveDocKey(docHint);
        }

        private static bool ContainsAnyLower(string hay, IEnumerable<string> needles)
        {
            if (string.IsNullOrWhiteSpace(hay) || needles == null)
                return false;

            foreach (var needle in needles)
            {
                if (string.IsNullOrWhiteSpace(needle))
                    continue;
                if (hay.Contains(needle, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
