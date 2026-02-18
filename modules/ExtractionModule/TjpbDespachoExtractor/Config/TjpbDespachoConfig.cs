using System;
using System.Collections.Generic;
using System.IO;
using Obj.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Obj.TjpbDespachoExtractor.Config
{
    public class TjpbDespachoConfig
    {
        public string Version { get; set; } = "2025-12-19";
        public string BaseDir { get; set; } = "";
        public ThresholdsConfig Thresholds { get; set; } = new ThresholdsConfig();
        public AnchorsConfig Anchors { get; set; } = new AnchorsConfig();
        public TemplateRegionsConfig TemplateRegions { get; set; } = new TemplateRegionsConfig();
        public DespachoTypeConfig DespachoType { get; set; } = new DespachoTypeConfig();
        public CertidaoConfig Certidao { get; set; } = new CertidaoConfig();
        public RegexConfig Regex { get; set; } = new RegexConfig();
        public PrioritiesConfig Priorities { get; set; } = new PrioritiesConfig();
        public FieldRulesConfig Fields { get; set; } = new FieldRulesConfig();
        public FieldStrategiesConfig FieldStrategies { get; set; } = new FieldStrategiesConfig();
        public ReferenceConfig Reference { get; set; } = new ReferenceConfig();

        public static TjpbDespachoConfig Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Config path is required", nameof(path));
            if (!File.Exists(path))
                throw new FileNotFoundException("Config file not found", path);

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            using var reader = new StreamReader(path);
            var cfg = deserializer.Deserialize<TjpbDespachoConfig>(reader);
            var loaded = cfg ?? new TjpbDespachoConfig();
            loaded.BaseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            loaded.ApplyRegistryDefaults();
            return loaded;
        }

        private sealed class DespachoTypeDoc
        {
            public List<string> AutorizacaoHints { get; set; } = new List<string>();
            public List<string> GeorcHints { get; set; } = new List<string>();
            public List<string> ConselhoHints { get; set; } = new List<string>();
            public List<string> DeValuePatterns { get; set; } = new List<string>();
        }

        private sealed class RegexDoc
        {
            public RegexConfig? Regex { get; set; }
        }

        private sealed class CertidaoDoc
        {
            public List<string> HeaderHints { get; set; } = new List<string>();
            public List<string> TitleHints { get; set; } = new List<string>();
            public List<string> BodyHints { get; set; } = new List<string>();
            public List<string> DateHints { get; set; } = new List<string>();
        }

        private void ApplyRegistryDefaults()
        {
            var path = PatternRegistry.FindFile("markers", "despacho_type.yml");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                path = "";

            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                if (!string.IsNullOrWhiteSpace(path))
                {
                    var doc = deserializer.Deserialize<DespachoTypeDoc>(File.ReadAllText(path));
                    if (doc != null)
                    {
                        if (DespachoType == null)
                            DespachoType = new DespachoTypeConfig();

                        FillIfEmpty(DespachoType.AutorizacaoHints, doc.AutorizacaoHints);
                        FillIfEmpty(DespachoType.GeorcHints, doc.GeorcHints);
                        FillIfEmpty(DespachoType.ConselhoHints, doc.ConselhoHints);
                        FillIfEmpty(DespachoType.DeValuePatterns, doc.DeValuePatterns);
                    }
                }

                var regexPath = PatternRegistry.FindFile("regex", "tjpb_base.yml");
                if (!string.IsNullOrWhiteSpace(regexPath) && File.Exists(regexPath))
                {
                    var regexDoc = deserializer.Deserialize<RegexDoc>(File.ReadAllText(regexPath));
                    if (regexDoc?.Regex != null)
                        FillRegexIfEmpty(Regex, regexDoc.Regex);
                }

                var prioritiesPath = PatternRegistry.FindFile("markers", "field_priorities.yml");
                if (!string.IsNullOrWhiteSpace(prioritiesPath) && File.Exists(prioritiesPath))
                {
                    var prioritiesDoc = deserializer.Deserialize<PrioritiesConfig>(File.ReadAllText(prioritiesPath));
                    if (prioritiesDoc != null)
                        FillPrioritiesIfEmpty(Priorities, prioritiesDoc);
                }

                var rulesPath = PatternRegistry.FindFile("markers", "field_rules.yml");
                if (!string.IsNullOrWhiteSpace(rulesPath) && File.Exists(rulesPath))
                {
                    var rulesDoc = deserializer.Deserialize<FieldRulesConfig>(File.ReadAllText(rulesPath));
                    if (rulesDoc != null)
                        FillFieldRulesIfEmpty(Fields, rulesDoc);
                }

                var certidaoPath = PatternRegistry.FindFile("markers", "certidao_hints.yml");
                if (!string.IsNullOrWhiteSpace(certidaoPath) && File.Exists(certidaoPath))
                {
                    var certDoc = deserializer.Deserialize<CertidaoDoc>(File.ReadAllText(certidaoPath));
                    if (certDoc != null)
                        FillCertidaoIfEmpty(Certidao, certDoc);
                }

                var signaturePath = PatternRegistry.FindFile("markers", "signature_hints.yml");
                if (!string.IsNullOrWhiteSpace(signaturePath) && File.Exists(signaturePath))
                {
                    var sigDoc = deserializer.Deserialize<AnchorsConfig>(File.ReadAllText(signaturePath));
                    if (sigDoc != null)
                        FillAnchorsIfEmpty(Anchors, sigDoc);
                }
            }
            catch
            {
                // Keep config values on errors.
            }
        }

        private static void FillIfEmpty(List<string> target, List<string> source)
        {
            if (target == null || target.Count > 0 || source == null || source.Count == 0)
                return;
            foreach (var item in source)
            {
                if (string.IsNullOrWhiteSpace(item))
                    continue;
                target.Add(item);
            }
        }

        private static void FillRegexIfEmpty(RegexConfig target, RegexConfig source)
        {
            if (target == null || source == null) return;
            if (string.IsNullOrWhiteSpace(target.ProcessoCnj)) target.ProcessoCnj = source.ProcessoCnj;
            if (string.IsNullOrWhiteSpace(target.ProcessoCnjLoose)) target.ProcessoCnjLoose = source.ProcessoCnjLoose;
            if (string.IsNullOrWhiteSpace(target.ProcessoSei)) target.ProcessoSei = source.ProcessoSei;
            if (string.IsNullOrWhiteSpace(target.ProcessoAdme)) target.ProcessoAdme = source.ProcessoAdme;
            if (string.IsNullOrWhiteSpace(target.Cpf)) target.Cpf = source.Cpf;
            if (string.IsNullOrWhiteSpace(target.Money)) target.Money = source.Money;
            if (string.IsNullOrWhiteSpace(target.DatePt)) target.DatePt = source.DatePt;
            if (string.IsNullOrWhiteSpace(target.DateSlash)) target.DateSlash = source.DateSlash;
        }

        private static void FillPrioritiesIfEmpty(PrioritiesConfig target, PrioritiesConfig source)
        {
            if (target == null || source == null) return;
            FillIfEmpty(target.ProcessoAdminLabels, source.ProcessoAdminLabels);
            FillIfEmpty(target.PeritoLabels, source.PeritoLabels);
            FillIfEmpty(target.VaraLabels, source.VaraLabels);
            FillIfEmpty(target.ComarcaLabels, source.ComarcaLabels);
            FillIfEmpty(target.PromoventeLabels, source.PromoventeLabels);
            FillIfEmpty(target.PromovidoLabels, source.PromovidoLabels);
        }

        private static void FillFieldRulesIfEmpty(FieldRulesConfig target, FieldRulesConfig source)
        {
            if (target == null || source == null) return;
            FillFieldRuleIfEmpty(target.ProcessoAdministrativo, source.ProcessoAdministrativo);
            FillFieldRuleIfEmpty(target.ProcessoJudicial, source.ProcessoJudicial);
            FillFieldRuleIfEmpty(target.Vara, source.Vara);
            FillFieldRuleIfEmpty(target.Comarca, source.Comarca);
            FillFieldRuleIfEmpty(target.Promovente, source.Promovente);
            FillFieldRuleIfEmpty(target.Promovido, source.Promovido);
            FillFieldRuleIfEmpty(target.Perito, source.Perito);
            FillFieldRuleIfEmpty(target.CpfPerito, source.CpfPerito);
            FillFieldRuleIfEmpty(target.Especialidade, source.Especialidade);
            FillFieldRuleIfEmpty(target.EspeciePericia, source.EspeciePericia);
            FillFieldRuleIfEmpty(target.ValorJz, source.ValorJz);
            FillFieldRuleIfEmpty(target.ValorDe, source.ValorDe);
            FillFieldRuleIfEmpty(target.ValorCm, source.ValorCm);
            FillFieldRuleIfEmpty(target.ValorTabela, source.ValorTabela);
            FillFieldRuleIfEmpty(target.Adiantamento, source.Adiantamento);
            FillFieldRuleIfEmpty(target.Percentual, source.Percentual);
            FillFieldRuleIfEmpty(target.Parcela, source.Parcela);
            FillFieldRuleIfEmpty(target.Data, source.Data);
            FillFieldRuleIfEmpty(target.Assinante, source.Assinante);
            FillFieldRuleIfEmpty(target.NumPerito, source.NumPerito);
        }

        private static void FillFieldRuleIfEmpty(FieldRuleConfig target, FieldRuleConfig source)
        {
            if (target == null || source == null) return;
            FillIfEmpty(target.Templates, source.Templates);
            FillIfEmpty(target.Labels, source.Labels);
            FillIfEmpty(target.Hints, source.Hints);
        }

        private static void FillCertidaoIfEmpty(CertidaoConfig target, CertidaoDoc source)
        {
            if (target == null || source == null) return;
            FillIfEmpty(target.HeaderHints, source.HeaderHints);
            FillIfEmpty(target.TitleHints, source.TitleHints);
            FillIfEmpty(target.BodyHints, source.BodyHints);
            FillIfEmpty(target.DateHints, source.DateHints);
        }

        private static void FillAnchorsIfEmpty(AnchorsConfig target, AnchorsConfig source)
        {
            if (target == null || source == null) return;
            FillIfEmpty(target.SignerHints, source.SignerHints);
        }
    }

    public class ThresholdsConfig
    {
        public double BlankMaxPct { get; set; } = 15;
        public int MinPages { get; set; } = 2;
        public int MaxPages { get; set; } = 6;
        public BandsConfig Bands { get; set; } = new BandsConfig();
        public ParagraphConfig Paragraph { get; set; } = new ParagraphConfig();
        public MatchConfig Match { get; set; } = new MatchConfig();
    }

    public class BandsConfig
    {
        public double HeaderTopPct { get; set; } = 0.15;
        public double SubheaderPct { get; set; } = 0.15;
        public double BodyStartPct { get; set; } = 0.30;
        public double FooterBottomPct { get; set; } = 0.15;
    }

    public class ParagraphConfig
    {
        public double LineMergeY { get; set; } = 0.015;
        public double ParagraphGapY { get; set; } = 0.03;
        public double WordGapX { get; set; } = 0.012;
    }

    public class MatchConfig
    {
        public double DocScoreMin { get; set; } = 0.70;
        public int AnchorsMin { get; set; } = 3;
    }

    public class AnchorsConfig
    {
        public List<string> Header { get; set; } = new List<string>();
        public List<string> Subheader { get; set; } = new List<string>();
        public List<string> Title { get; set; } = new List<string>();
        public List<string> Footer { get; set; } = new List<string>();
        public List<string> SignerHints { get; set; } = new List<string>();
    }

    public class TemplateRegionsConfig
    {
        public RegionTemplateConfig FirstPageTop { get; set; } = new RegionTemplateConfig { MinY = 0.55, MaxY = 1.0 };
        public RegionTemplateConfig LastPageBottom { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 0.45 };
        public RegionTemplateConfig CertidaoFull { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 1.0 };
        public RegionTemplateConfig CertidaoValueDate { get; set; } = new RegionTemplateConfig { MinY = 0.0, MaxY = 1.0 };
        public double WordGapX { get; set; } = 0.012;
    }

    public class DespachoTypeConfig
    {
        public List<string> AutorizacaoHints { get; set; } = new List<string>();
        public List<string> GeorcHints { get; set; } = new List<string>();
        public List<string> ConselhoHints { get; set; } = new List<string>();
        public List<string> DeValuePatterns { get; set; } = new List<string>();
    }

    public class CertidaoConfig
    {
        public List<string> HeaderHints { get; set; } = new List<string>();
        public List<string> TitleHints { get; set; } = new List<string>();
        public List<string> BodyHints { get; set; } = new List<string>();
        public List<string> DateHints { get; set; } = new List<string>();
    }

    public class RegionTemplateConfig
    {
        public double MinY { get; set; } = 0.0;
        public double MaxY { get; set; } = 1.0;
        public List<string> Templates { get; set; } = new List<string>();
    }

    public class RegexConfig
    {
        public string ProcessoCnj { get; set; } = "";
        public string ProcessoCnjLoose { get; set; } = "";
        public string ProcessoSei { get; set; } = "";
        public string ProcessoAdme { get; set; } = "";
        public string Cpf { get; set; } = "";
        public string Money { get; set; } = "";
        public string DatePt { get; set; } = "";
        public string DateSlash { get; set; } = "";
    }

    public class PrioritiesConfig
    {
        public List<string> ProcessoAdminLabels { get; set; } = new List<string>();
        public List<string> PeritoLabels { get; set; } = new List<string>();
        public List<string> VaraLabels { get; set; } = new List<string>();
        public List<string> ComarcaLabels { get; set; } = new List<string>();
        public List<string> PromoventeLabels { get; set; } = new List<string>();
        public List<string> PromovidoLabels { get; set; } = new List<string>();
    }

    public class FieldRulesConfig
    {
        public FieldRuleConfig ProcessoAdministrativo { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ProcessoJudicial { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Vara { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Comarca { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Promovente { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Promovido { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Perito { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig CpfPerito { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Especialidade { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig EspeciePericia { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorJz { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorDe { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorCm { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig ValorTabela { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Adiantamento { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Percentual { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Parcela { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Data { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig Assinante { get; set; } = new FieldRuleConfig();
        public FieldRuleConfig NumPerito { get; set; } = new FieldRuleConfig();
    }

    public class FieldRuleConfig
    {
        public List<string> Templates { get; set; } = new List<string>();
        public List<string> Labels { get; set; } = new List<string>();
        public List<string> Hints { get; set; } = new List<string>();
    }

    public class ReferenceConfig
    {
        public List<string> PeritosCatalogPaths { get; set; } = new List<string>();
        public HonorariosConfig Honorarios { get; set; } = new HonorariosConfig();
    }

    public class HonorariosConfig
    {
        public string? TablePath { get; set; }
        public string? AliasesPath { get; set; }
        public List<HonorariosAreaMap> AreaMap { get; set; } = new List<HonorariosAreaMap>();
        public double ValueTolerancePct { get; set; } = 0.15;
        public bool PreferValorDe { get; set; } = true;
        public bool AllowValorJz { get; set; } = false;
    }

    public class HonorariosAreaMap
    {
        public string Area { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }
}
