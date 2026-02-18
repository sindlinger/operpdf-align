using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Obj.Utils
{
    public static class InputPreview
    {
        private sealed class PreviewDefaults
        {
            public bool Enabled { get; set; } = true;
            public int MaxList { get; set; } = 200;
        }

        public static void PrintPlannedInputs(string[] args)
        {
            if (args == null || args.Length == 0)
                return;

            if (args.Any(a => a.Equals("--no-preview", StringComparison.OrdinalIgnoreCase)))
                return;

            var defaults = LoadDefaults();
            if (!defaults.Enabled)
                return;

            var inputs = ResolveInputs(args);
            if (inputs.Count == 0)
                return;

            int maxList = defaults.MaxList > 0 ? defaults.MaxList : inputs.Count;
            int shown = Math.Min(inputs.Count, maxList);

            Console.WriteLine($"[PLANNED] total={inputs.Count} showing={shown}");
            for (int i = 0; i < shown; i++)
                Console.WriteLine($"  {i + 1:D3} {inputs[i]}");
            if (inputs.Count > shown)
                Console.WriteLine($"  ... (+{inputs.Count - shown} arquivos)");
            Console.WriteLine();
        }

        private static List<string> ResolveInputs(string[] args)
        {
            var inputs = new List<string>();
            string? input = GetArgValue(args, "--input");
            string? inputDir = GetArgValue(args, "--input-dir");
            string? inputsList = GetArgValue(args, "--inputs");
            string? inputFile = GetArgValue(args, "--input-file");
            string? manifest = GetArgValue(args, "--input-manifest");
            int limit = GetIntArg(args, "--limit");

            if (!string.IsNullOrWhiteSpace(inputsList))
                inputs.AddRange(SplitInputs(inputsList));
            if (!string.IsNullOrWhiteSpace(input))
                inputs.AddRange(ExpandSingle(input));
            if (!string.IsNullOrWhiteSpace(inputDir))
                inputs.AddRange(ExpandSingle(inputDir));
            if (!string.IsNullOrWhiteSpace(inputFile))
                inputs.AddRange(ReadListFile(inputFile));
            if (!string.IsNullOrWhiteSpace(manifest))
                inputs.AddRange(ReadListFile(manifest));

            inputs = inputs.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (limit > 0 && inputs.Count > limit)
                inputs = inputs.Take(limit).ToList();
            return inputs;
        }

        private static List<string> ExpandSingle(string value)
        {
            if (File.Exists(value))
                return new List<string> { value };
            if (Directory.Exists(value))
                return Directory.GetFiles(value, "*.pdf")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            return new List<string>();
        }

        private static List<string> SplitInputs(string raw)
        {
            var list = new List<string>();
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length == 0) continue;
                list.AddRange(ExpandSingle(token));
            }
            return list;
        }

        private static List<string> ReadListFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new List<string>();
                var lines = File.ReadAllLines(path)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                    .ToList();
                var outList = new List<string>();
                foreach (var line in lines)
                    outList.AddRange(ExpandSingle(line));
                return outList;
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string? GetArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static int GetIntArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(args[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
                        return v;
                }
            }
            return 0;
        }

        private static PreviewDefaults LoadDefaults()
        {
            var defaults = new PreviewDefaults();
            var path = Path.Combine("configs", "operpdf.json");
            if (!File.Exists(path))
                return defaults;
            try
            {
                var json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                if (!TryGetPropertyIgnoreCase(doc.RootElement, "preview_inputs", out var node) &&
                    !TryGetPropertyIgnoreCase(doc.RootElement, "previewInputs", out node))
                    return defaults;
                if (TryGetPropertyIgnoreCase(node, "enabled", out var v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.Enabled = v.GetBoolean();
                if (TryGetPropertyIgnoreCase(node, "max_list", out v) || TryGetPropertyIgnoreCase(node, "maxList", out v))
                    if (v.ValueKind == JsonValueKind.Number) defaults.MaxList = v.GetInt32();
            }
            catch
            {
                return defaults;
            }
            return defaults;
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
