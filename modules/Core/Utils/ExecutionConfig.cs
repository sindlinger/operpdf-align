using System;
using System.IO;
using System.Text.Json;

namespace Obj.Utils
{
    public static class ExecutionConfig
    {
        private static readonly object Lock = new();
        private static JsonDocument? _doc;
        private static DateTime _lastWriteUtc;

        public sealed class ExecDefaults
        {
            public int Jobs { get; set; } = 1;
            public double TimeoutSec { get; set; } = 0;
            public bool? Debug { get; set; }
            public bool? Log { get; set; }
            public bool? ForceGc { get; set; }
            public string? ReportFile { get; set; }
        }

        public sealed class ProgressDefaults
        {
            public bool Enabled { get; set; }
            public int Every { get; set; } = 10;
        }

        public sealed class PreflightDefaults
        {
            public bool Enabled { get; set; } = true;
            public int Jobs { get; set; } = 4;
            public double TimeoutSec { get; set; } = 20;
            public bool Log { get; set; } = true;
        }

        public static ExecDefaults GetExecDefaults(string? section = null)
        {
            var defaults = new ExecDefaults();
            ApplyExecSection("exec", defaults);
            if (!string.IsNullOrWhiteSpace(section))
                ApplyExecSection(section!, defaults);
            return defaults;
        }

        public static ProgressDefaults GetProgressDefaults()
        {
            var defaults = new ProgressDefaults();
            if (!TryGetSection("progress", out var node) &&
                !TryGetSection("progresso", out node))
                return defaults;
            if (TryGetPropertyIgnoreCase(node, "enabled", out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.Enabled = v.GetBoolean();
            if (TryGetPropertyIgnoreCase(node, "every", out v) && v.ValueKind == JsonValueKind.Number)
                defaults.Every = v.GetInt32();
            return defaults;
        }

        public static PreflightDefaults GetPreflightDefaults()
        {
            var defaults = new PreflightDefaults();
            if (TryGetSection("exec", out _))
            {
                var exec = GetExecDefaults();
                if (exec.Jobs > 0)
                    defaults.Jobs = exec.Jobs;
                if (exec.TimeoutSec > 0)
                    defaults.TimeoutSec = exec.TimeoutSec;
                if (exec.Log.HasValue)
                    defaults.Log = exec.Log.Value;
            }
            if (!TryGetSection("preflight", out var node))
                return defaults;
            if (TryGetPropertyIgnoreCase(node, "enabled", out var v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.Enabled = v.GetBoolean();
            if (TryGetPropertyIgnoreCase(node, "jobs", out v) && v.ValueKind == JsonValueKind.Number)
                defaults.Jobs = v.GetInt32();
            if (TryGetPropertyIgnoreCase(node, "timeout_sec", out v) && v.ValueKind == JsonValueKind.Number)
                defaults.TimeoutSec = v.GetDouble();
            if (TryGetPropertyIgnoreCase(node, "log", out v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.Log = v.GetBoolean();
            return defaults;
        }

        public static bool TryGetSection(string name, out JsonElement node)
        {
            node = default;
            var doc = LoadDoc();
            if (doc == null)
                return false;
            if (!TryGetPropertyIgnoreCase(doc.RootElement, name, out node))
                return false;
            return node.ValueKind == JsonValueKind.Object;
        }

        private static void ApplyExecSection(string name, ExecDefaults defaults)
        {
            if (!TryGetSection(name, out var node))
                return;

            if (TryGetPropertyIgnoreCase(node, "jobs", out var v) && v.ValueKind == JsonValueKind.Number)
                defaults.Jobs = Math.Max(1, v.GetInt32());
            if (TryGetPropertyIgnoreCase(node, "timeout_sec", out v) && v.ValueKind == JsonValueKind.Number)
                defaults.TimeoutSec = Math.Max(0, v.GetDouble());
            if (TryGetPropertyIgnoreCase(node, "debug", out v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.Debug = v.GetBoolean();
            if (TryGetPropertyIgnoreCase(node, "log", out v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.Log = v.GetBoolean();
            if (TryGetPropertyIgnoreCase(node, "force_gc", out v) &&
                (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                defaults.ForceGc = v.GetBoolean();
            if (TryGetPropertyIgnoreCase(node, "report_file", out v) && v.ValueKind == JsonValueKind.String)
                defaults.ReportFile = v.GetString();
        }

        private static JsonDocument? LoadDoc()
        {
            var path = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(path))
                return null;

            lock (Lock)
            {
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (_doc != null && lastWrite == _lastWriteUtc)
                    return _doc;

                try
                {
                    var json = File.ReadAllText(path);
                    _doc?.Dispose();
                    _doc = JsonDocument.Parse(json);
                    _lastWriteUtc = lastWrite;
                    return _doc;
                }
                catch
                {
                    return null;
                }
            }
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement node, string name, out JsonElement value)
        {
            foreach (var p in node.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }
    }
}
