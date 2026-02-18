using System;
using System.Collections.Generic;

namespace Obj.TjpbDespachoExtractor.Config
{
    public class FieldStrategiesConfig
    {
        public bool Enabled { get; set; } = true;
        public string? Dir { get; set; }
        public List<string> Files { get; set; } = new List<string>();
    }

    public class FieldStrategyDefinition
    {
        public List<string> Fields { get; set; } = new List<string>();
        public double Priority { get; set; } = 1.0;
        public List<FieldStrategySource> Sources { get; set; } = new List<FieldStrategySource>();
        public List<FieldStrategyPattern> Patterns { get; set; } = new List<FieldStrategyPattern>();
        public List<string> Fallback { get; set; } = new List<string>();
        public List<string> Clean { get; set; } = new List<string>();
        public List<string> Validate { get; set; } = new List<string>();
    }

    public class FieldStrategySource
    {
        public string Bucket { get; set; } = "";
        public List<string> NameMatches { get; set; } = new List<string>();
    }

    public class FieldStrategyPattern
    {
        public string Type { get; set; } = "regex";
        public string Label { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Field { get; set; } = "";
        public double Weight { get; set; } = 1.0;
        public string? Value { get; set; }
        public FieldStrategyCompose? Compose { get; set; }
    }

    public class FieldStrategyCompose
    {
        public string SecondField { get; set; } = "";
    }
}
