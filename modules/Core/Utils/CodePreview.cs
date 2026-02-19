using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Obj.Utils
{
    public static class CodePreview
    {
        private sealed class PreviewDefaults
        {
            public bool Enabled { get; set; } = true;
        }

        private static readonly Dictionary<string, string[]> CommandFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = new[]
            {
                "src/Commands/Inspect/ObjectsOperators.cs",
                "modules/TextOpsRanges/*",
                "modules/Align/*",
                "modules/Core/*",
                "modules/ExtractionModule/*"
            },
            ["align"] = new[]
            {
                "src/Commands/Inspect/ObjectsOperators.cs",
                "modules/TextOpsRanges/*",
                "modules/Align/*",
                "modules/Core/*"
            },
            ["anchors"] = new[]
            {
                "src/Commands/Inspect/ObjectsOperators.cs",
                "modules/Align/*",
                "modules/TextOpsRanges/*",
                "modules/Core/*"
            },
            ["weirdspace"] = new[]
            {
                "src/Commands/Inspect/ObjectsOperators.cs",
                "src/Commands/Inspect/PdfTextExtraction.cs",
                "modules/Core/*"
            },
            ["diff"] = new[]
            {
                "modules/TextOpsRanges/*"
            },
            ["textopsvar"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/Align/*",
                "modules/TextOpsRanges/*",
                "modules/HonorariosModule/*",
                "modules/ValidatorModule/*",
                "modules/ValidationCore/*"
            },
            ["textopsfixed"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/Align/*",
                "modules/TextOpsRanges/*",
                "modules/HonorariosModule/*",
                "modules/ValidatorModule/*",
                "modules/ValidationCore/*"
            },
            ["textopsdiff"] = new[]
            {
                "modules/TextOpsRanges/*"
            },
            ["textopsalign"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/Align/*",
                "modules/TextOpsRanges/*",
                "modules/HonorariosModule/*",
                "modules/ValidatorModule/*",
                "modules/ValidationCore/*"
            },
            ["objdiff"] = new[]
            {
                "src/Commands/Inspect/ObjectsObjDiff.cs",
                "modules/Core/*"
            },
            ["pattern"] = new[]
            {
                "src/Commands/Inspect/ObjectsPattern.cs",
                "modules/PatternModules/*",
                "modules/ExtractionModule/*",
                "modules/Core/*",
                "modules/PatternModules/registry/patterns/*",
                "modules/PatternModules/registry/template_fields/*"
            },
            ["honorarios"] = new[]
            {
                "src/Commands/Inspect/ObjectsHonorariosApi.cs",
                "modules/HonorariosModule/*",
                "modules/ExtractionModule/TjpbDespachoExtractor/Reference/*"
            },
        };

        public static void PrintPlannedCode(string command, string[] args)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;
            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg.Equals("--no-code-preview", StringComparison.OrdinalIgnoreCase))
                        return;
                }
            }

            var defaults = LoadDefaults();
            if (!defaults.Enabled)
                return;

            if (!CommandFiles.TryGetValue(command, out var files) || files.Length == 0)
                return;

            Console.WriteLine("[CODE]");
            var printed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var mode = ResolveTraceMode(args);
            if (mode == "off")
                return;
            var entries = mode == "brief" ? files : ExpandEntries(files);
            foreach (var f in entries)
            {
                if (printed.Add(f))
                    Console.WriteLine($"  {f}");
            }
            Console.WriteLine();
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
                if (!TryGetPropertyIgnoreCase(doc.RootElement, "preview_code", out var node) &&
                    !TryGetPropertyIgnoreCase(doc.RootElement, "previewCode", out node))
                    return defaults;
                if (TryGetPropertyIgnoreCase(node, "enabled", out var v) &&
                    (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                    defaults.Enabled = v.GetBoolean();
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

        private static string ResolveTraceMode(string[]? args)
        {
            if (args == null)
                return "brief";

            foreach (var arg in args)
            {
                if (arg == null)
                    continue;
                if (arg.Equals("--trace-code", StringComparison.OrdinalIgnoreCase))
                    return "full";
                if (arg.StartsWith("--trace-code=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg.Substring("--trace-code=".Length).Trim();
                    if (value.Equals("brief", StringComparison.OrdinalIgnoreCase))
                        return "brief";
                    if (value.Equals("full", StringComparison.OrdinalIgnoreCase))
                        return "full";
                    if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                        value.Equals("none", StringComparison.OrdinalIgnoreCase))
                        return "off";
                }
            }

            return "brief";
        }

        private static IEnumerable<string> ExpandEntries(IEnumerable<string> entries)
        {
            foreach (var entry in entries)
            {
                foreach (var resolved in ExpandEntry(entry))
                    yield return resolved;
            }
        }

        private static IEnumerable<string> ExpandEntry(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry))
                yield break;

            if (entry.EndsWith("/*", StringComparison.Ordinal))
            {
                var dir = entry[..^2];
                foreach (var file in ExpandDirectory(dir))
                    yield return file;
                yield break;
            }

            if (entry.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                var dir = Path.GetDirectoryName(entry);
                var pattern = Path.GetFileName(entry);
                if (string.IsNullOrWhiteSpace(dir))
                    dir = ".";
                foreach (var file in ExpandDirectory(dir, pattern))
                    yield return file;
                yield break;
            }

            if (Directory.Exists(entry))
            {
                foreach (var file in ExpandDirectory(entry))
                    yield return file;
                yield break;
            }

            if (File.Exists(entry))
            {
                yield return entry;
                yield break;
            }

            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, entry);
            if (Directory.Exists(candidate))
            {
                foreach (var file in ExpandDirectory(candidate))
                    yield return file;
                yield break;
            }
            if (File.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }

            yield return entry;
        }

        private static IEnumerable<string> ExpandDirectory(string dir, string? pattern = null)
        {
            if (!Directory.Exists(dir))
                yield break;

            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".cs", ".json", ".yml", ".yaml", ".md"
            };

            var files = string.IsNullOrWhiteSpace(pattern)
                ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                : Directory.GetFiles(dir, pattern, SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!extensions.Contains(ext))
                    continue;
                yield return NormalizePath(file);
            }
        }

        private static string NormalizePath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/');
        }
    }
}
