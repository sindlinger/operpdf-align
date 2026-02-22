using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.IO;

namespace Obj.Utils
{
    public static class PathUtils
    {
        private enum PathStyle
        {
            Auto,
            Windows,
            Wsl
        }

        private static PathStyle _preferredOutputStyle = PathStyle.Auto;

        private static readonly HashSet<string> PathFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--input", "--input-dir", "--input-file", "--input-manifest",
            "--out", "--out-dir", "--output", "--config", "--db", "--cache",
            "--cache-dir", "--export", "--export-doc-dtos",
            "--report",
            "--template", "--annotated", "--from-nlp", "--text",
            "--out-annotated", "--out-plan", "--out-json",
            "--model", "--inputs", "--templates", "--templates-dir",
            "--align-model",
            "--tee"
        };

        private static readonly HashSet<string> DevPickFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--input", "--input-file", "--model", "--inputs", "--templates"
        };

        private static readonly HashSet<string> NonPathValueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "--page", "--pages", "--page-range", "--pageA", "--pageB", "--page-a", "--page-b",
            "--obj", "--objA", "--objB",
            "--top", "--limit", "--limits", "--offset",
            "--min-score", "--max-score", "--backoff",
            "--min-ratio", "--max-pieces", "--footer-max-ops", "--line-tol", "--min-block-len",
            "--phrase"
        };

        public static string NormalizePathForCurrentOS(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";

            if (IsWindows())
            {
                if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && string.Equals(parts[0], "mnt", StringComparison.OrdinalIgnoreCase))
                    {
                        var drive = parts[1];
                        if (drive.Length == 1)
                        {
                            var rest = string.Join("\\", parts.Skip(2));
                            return $"{drive.ToUpperInvariant()}:\\{rest}";
                        }
                    }
                }
                return path;
            }

            if (IsWSL())
            {
                var m = Regex.Match(path, @"^(?<drive>[A-Za-z]):[\\/](?<rest>.*)$");
                if (m.Success)
                {
                    var drive = m.Groups["drive"].Value.ToLowerInvariant();
                    var rest = m.Groups["rest"].Value.Replace('\\', '/');
                    return $"/mnt/{drive}/{rest}";
                }
            }

            return path;
        }

        public static string[] NormalizeArgs(string[] args)
        {
            if (args == null || args.Length == 0) return args ?? Array.Empty<string>();
            var outArgs = new List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (NonPathValueFlags.Contains(arg) && i + 1 < args.Length)
                {
                    outArgs.Add(arg);
                    outArgs.Add(args[++i]);
                    continue;
                }
                if (PathFlags.Contains(arg) && i + 1 < args.Length)
                {
                    outArgs.Add(arg);
                    var value = args[++i];
                    if (DevPickFlags.Contains(arg))
                    {
                        if (string.Equals(arg, "--inputs", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(arg, "--templates", StringComparison.OrdinalIgnoreCase))
                            value = ResolveDevInputs(value);
                        else
                            value = ResolveDevPick(value);
                    }
                    value = ResolveIndexVariable(ResolveModelVariable(value));
                    TrackPreferredStyle(value);
                    outArgs.Add(NormalizePathForCurrentOS(value));
                    continue;
                }

                if (!arg.StartsWith("-", StringComparison.OrdinalIgnoreCase))
                {
                    if (!LooksLikePath(arg) && IsDevEnabled() && LooksLikePick(arg))
                    {
                        arg = ResolveDevPickToken(arg, allowEnvPick: false);
                    }
                    arg = ResolveIndexVariable(ResolveModelVariable(arg));
                    if (LooksLikePath(arg))
                    {
                        TrackPreferredStyle(arg);
                        outArgs.Add(NormalizePathForCurrentOS(arg));
                        continue;
                    }
                }

                outArgs.Add(arg);
            }
            return outArgs.ToArray();
        }

        private static string ResolveDevPick(string value)
        {
            if (!IsDevEnabled())
                return value;

            if (string.IsNullOrWhiteSpace(value))
                return value;

            return ResolveDevPickToken(value, allowEnvPick: true);
        }

        private static string ResolveDevInputs(string value)
        {
            if (!IsDevEnabled())
                return value;

            if (string.IsNullOrWhiteSpace(value))
                return value;

            if (!value.Contains(",", StringComparison.Ordinal) && !value.Contains("-", StringComparison.Ordinal))
                return ResolveDevPickToken(value, allowEnvPick: true);

            var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var resolved = new List<string>(parts.Length);
            foreach (var raw in parts)
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                if (TryParseRange(token, out var start, out var end))
                {
                    if (end < start)
                    {
                        var tmp = start;
                        start = end;
                        end = tmp;
                    }
                    for (int i = start; i <= end; i++)
                        resolved.Add(ResolveDevPickToken(i.ToString(), allowEnvPick: false));
                    continue;
                }
                resolved.Add(ResolveDevPickToken(token, allowEnvPick: false));
            }
            return string.Join(",", resolved);
        }

        private static string ResolveModelVariable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            if (value.Contains(",", StringComparison.Ordinal))
            {
                var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var resolved = new List<string>(parts.Length);
                foreach (var raw in parts)
                {
                    var token = raw.Trim();
                    if (token.Length == 0) continue;
                    resolved.Add(ResolveModelVariable(token));
                }
                return string.Join(",", resolved);
            }

            var v = value.Trim();
            var aliasResolved = ResolveTypedModelAliasToken(v);
            if (!string.Equals(aliasResolved, v, StringComparison.Ordinal))
                return aliasResolved;

            // Alias global @MODEL foi desativado. Use somente aliases tipados (@M-DESP/@M-CER/@M-REQ).
            return value;
        }

        private enum ModelAliasKind
        {
            Despacho,
            Certidao,
            Requerimento
        }

        private static string ResolveTypedModelAliasToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return token ?? "";

            var t = token.Trim();
            if (t.Equals("@M-DES", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("@M-DESP", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("@M-DESPACHO", StringComparison.OrdinalIgnoreCase))
            {
                var values = ResolveTypedModelAlias(ModelAliasKind.Despacho);
                return values.Count > 0 ? string.Join(",", values) : token;
            }

            if (t.Equals("@M-CER", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("@M-CERTIDAO", StringComparison.OrdinalIgnoreCase))
            {
                var values = ResolveTypedModelAlias(ModelAliasKind.Certidao);
                return values.Count > 0 ? string.Join(",", values) : token;
            }

            if (t.Equals("@M-REQ", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("@M-REQUERIMENTO", StringComparison.OrdinalIgnoreCase))
            {
                var values = ResolveTypedModelAlias(ModelAliasKind.Requerimento);
                return values.Count > 0 ? string.Join(",", values) : token;
            }

            return token;
        }

        private static List<string> ResolveTypedModelAlias(ModelAliasKind kind)
        {
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in ResolveTypedModelDirs(kind))
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.pdf", SearchOption.AllDirectories)
                    .OrderBy(GetTypedAliasPdfPriority)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    if (IsExcludedTypedAliasPdf(file))
                        continue;
                    if (!IsValidPdfFile(file))
                        continue;
                    if (seen.Add(file))
                        files.Add(file);
                }
            }

            return files;
        }

        private static int GetTypedAliasPdfPriority(string path)
        {
            var name = (Path.GetFileName(path) ?? "").ToLowerInvariant();
            var priority = 0;

            // Keep canonical model names first, and push maintenance artifacts to the end.
            if (name.EndsWith("_model.pdf", StringComparison.Ordinal) ||
                name.EndsWith("-model.pdf", StringComparison.Ordinal) ||
                name.EndsWith("model.pdf", StringComparison.Ordinal))
            {
                priority -= 20;
            }

            if (name.EndsWith(".masked.pdf", StringComparison.Ordinal))
                priority += 300;

            if (name.EndsWith(".backup.pdf", StringComparison.Ordinal))
                priority += 400;

            if (name.Contains(".tmp.", StringComparison.Ordinal) ||
                name.StartsWith("tmp", StringComparison.Ordinal) ||
                name.Contains("draft", StringComparison.Ordinal))
            {
                priority += 500;
            }

            return priority;
        }

        private static bool IsExcludedTypedAliasPdf(string path)
        {
            var name = (Path.GetFileName(path) ?? "").ToLowerInvariant();
            if (name.Length == 0)
                return false;

            // Arquivos de manutenção não participam da seleção automática por alias.
            if (name.EndsWith(".masked.pdf", StringComparison.Ordinal))
                return true;
            if (name.EndsWith(".backup.pdf", StringComparison.Ordinal))
                return true;
            if (name.Contains(".tmp.", StringComparison.Ordinal))
                return true;
            if (name.StartsWith("tmp", StringComparison.Ordinal))
                return true;
            if (name.Contains("draft", StringComparison.Ordinal))
                return true;

            return false;
        }

        private static IEnumerable<string> ResolveTypedModelDirs(ModelAliasKind kind)
        {
            // Modo estrito: cada tipo usa somente um diretório explícito de alias.
            var envKey = kind switch
            {
                ModelAliasKind.Despacho => "OBJPDF_ALIAS_M_DES_DIR",
                ModelAliasKind.Certidao => "OBJPDF_ALIAS_M_CER_DIR",
                _ => "OBJPDF_ALIAS_M_REQ_DIR"
            };

            var raw = Environment.GetEnvironmentVariable(envKey);
            if (string.IsNullOrWhiteSpace(raw))
                yield break;

            var normalized = NormalizePathForCurrentOS(raw.Trim());
            if (string.IsNullOrWhiteSpace(normalized))
                yield break;

            string full;
            try
            {
                full = Path.GetFullPath(normalized);
            }
            catch
            {
                full = normalized;
            }

            yield return full;
        }

        private static string ResolveIndexVariable(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value ?? "";

            if (value.Contains(",", StringComparison.Ordinal))
            {
                var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                var resolved = new List<string>(parts.Length);
                foreach (var raw in parts)
                {
                    var token = raw.Trim();
                    if (token.Length == 0) continue;
                    resolved.Add(ResolveIndexVariable(token));
                }
                return string.Join(",", resolved);
            }

            if (!TryParseIndexToken(value, out var key, out var start, out var end))
                return value;

            if (!TryResolveIndexDir(key, out var dir))
                return value;

            if (end <= 0) end = start;
            if (end < start)
            {
                var tmp = start;
                start = end;
                end = tmp;
            }

            var isOutputs = string.Equals(key, "O", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(key, "J", StringComparison.OrdinalIgnoreCase);
            var files = Directory
                .EnumerateFiles(dir, isOutputs ? "*" : "*.pdf", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count == 0)
                return value;

            if (!isOutputs)
            {
                var validFiles = new List<string>(files.Count);
                var invalidCount = 0;
                foreach (var file in files)
                {
                    if (IsValidPdfFile(file))
                        validFiles.Add(file);
                    else
                        invalidCount++;
                }

                files = validFiles;
                if (files.Count == 0)
                    return value;
                if (invalidCount > 0)
                {
                    if (!ReturnUtils.IsEnabled())
                        Console.Error.WriteLine($"[INDEX {key}] ignorados {invalidCount} arquivos invalidos");
                }
            }

            var resolvedList = new List<string>();

            for (int i = start; i <= end; i++)
            {
                if (i < 1 || i > files.Count)
                {
                    if (!ReturnUtils.IsEnabled())
                        Console.Error.WriteLine($"[INDEX {key}] Indice invalido: {i}. Total arquivos: {files.Count}");
                    continue;
                }

                var selectedPath = files[i - 1];
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[INDEX {key}] pick {i}/{files.Count} -> {selectedPath}");
                resolvedList.Add(selectedPath);
            }

            if (resolvedList.Count == 0)
                return value;

            return string.Join(",", resolvedList);
        }


        private static bool TryParseIndexToken(string value, out string key, out int start, out int end)
        {
            key = "";
            start = 0;
            end = 0;
            var v = value.Trim();
            if (!v.StartsWith(":", StringComparison.Ordinal))
                return false;

            var match = Regex.Match(v, @"^:([A-Za-z])([0-9]+)(?:(?:-|\.\.)([0-9]+))?$");
            if (!match.Success)
                return false;

            key = match.Groups[1].Value.ToUpperInvariant();
            if (!int.TryParse(match.Groups[2].Value, out start))
                return false;
            if (match.Groups[3].Success)
                int.TryParse(match.Groups[3].Value, out end);
            return true;
        }

        private static bool TryResolveIndexDir(string key, out string dir)
        {
            dir = "";
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (string.Equals(key, "Q", StringComparison.OrdinalIgnoreCase))
            {
                var envKeyOut = Environment.GetEnvironmentVariable("OBJPDF_ALIAS_Q_DIR");
                if (!string.IsNullOrWhiteSpace(envKeyOut))
                {
                    dir = NormalizePathForCurrentOS(envKeyOut);
                    return true;
                }
                var qDir = Environment.GetEnvironmentVariable("OBJPDF_QUARENTENA_DIR");
                if (!string.IsNullOrWhiteSpace(qDir))
                {
                    dir = NormalizePathForCurrentOS(qDir);
                    return true;
                }
                return false;
            }

            var envKey = "OBJPDF_ALIAS_" + key.Trim().ToUpperInvariant() + "_DIR";
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                dir = NormalizePathForCurrentOS(envValue);
                return true;
            }
            return false;
        }

        private static bool TrySelectValidPdf(
            List<string> pdfs,
            int startIndex,
            HashSet<int>? usedIndexes,
            out int selectedIndex,
            out string selectedPath,
            out int skipped,
            out bool fallbackBack)
        {
            selectedIndex = -1;
            selectedPath = "";
            skipped = 0;
            fallbackBack = false;

            if (pdfs == null || pdfs.Count == 0)
                return false;

            for (int i = startIndex; i < pdfs.Count; i++)
            {
                if (usedIndexes != null && usedIndexes.Contains(i))
                {
                    skipped++;
                    continue;
                }
                if (IsValidPdfFile(pdfs[i]))
                {
                    selectedIndex = i;
                    selectedPath = pdfs[i];
                    usedIndexes?.Add(i);
                    return true;
                }
                skipped++;
            }

            for (int i = Math.Min(startIndex - 1, pdfs.Count - 1); i >= 0; i--)
            {
                if (usedIndexes != null && usedIndexes.Contains(i))
                    continue;
                if (IsValidPdfFile(pdfs[i]))
                {
                    selectedIndex = i;
                    selectedPath = pdfs[i];
                    fallbackBack = true;
                    usedIndexes?.Add(i);
                    return true;
                }
            }

            return false;
        }

        public static bool TryGetIndexInAliasDir(string key, string path, out int index, out int total)
        {
            index = 0;
            total = 0;
            if (!TryResolveIndexDir(key, out var dir))
                return false;
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(path))
                return false;

            var fullDir = Path.GetFullPath(dir);
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase))
                return false;

            var isOutputs = false;
            var files = Directory
                .EnumerateFiles(fullDir, isOutputs ? "*" : "*.pdf", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            total = files.Count;
            if (total == 0)
                return false;

            var matchIndex = files.FindIndex(p => Path.GetFullPath(p)
                .Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (matchIndex < 0)
                return false;

            index = matchIndex + 1;
            return true;
        }

        private static bool IsValidPdfFile(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return false;
                var info = new FileInfo(path);
                if (info.Length < 5)
                    return false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> header = stackalloc byte[5];
                var read = fs.Read(header);
                if (read < 5)
                    return false;
                return header[0] == (byte)'%' &&
                       header[1] == (byte)'P' &&
                       header[2] == (byte)'D' &&
                       header[3] == (byte)'F' &&
                       header[4] == (byte)'-';
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParseRange(string token, out int start, out int end)
        {
            start = 0;
            end = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;
            var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) return false;
            return int.TryParse(parts[0].Trim(), out start) && int.TryParse(parts[1].Trim(), out end);
        }

        private static bool LooksLikePick(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.Equals("random", StringComparison.OrdinalIgnoreCase)) return true;
            return int.TryParse(value, out _);
        }

        private static string ResolveDevPickToken(string value, bool allowEnvPick)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            var devDir = GetDevDir();
            if (string.IsNullOrWhiteSpace(devDir) || !Directory.Exists(devDir))
                return value;

            var pdfs = Directory
                .EnumerateFiles(devDir, "*.pdf", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (pdfs.Count == 0)
                return value;

            int? pick = null;
            if (string.Equals(value, "random", StringComparison.OrdinalIgnoreCase))
            {
                pick = new Random().Next(1, pdfs.Count + 1);
            }
            else if (int.TryParse(value, out var idx))
            {
                pick = idx;
            }
            else if (IsDevRandom())
            {
                pick = new Random().Next(1, pdfs.Count + 1);
            }
            else
            {
                if (!allowEnvPick)
                    return value;
                var envPick = GetEnvPick();
                if (envPick.HasValue && !LooksLikePath(value) && !File.Exists(value) && !Directory.Exists(value))
                    pick = envPick.Value;
            }

            if (pick == null)
                return value;

            if (pick < 1 || pick > pdfs.Count)
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[DEV] Indice invalido: {pick}. Total PDFs: {pdfs.Count}");
                return value;
            }

            if (!TrySelectValidPdf(pdfs, pick.Value - 1, null, out var selectedIndex, out var selected, out var skipped, out var fallbackBack))
                return value;

            if (skipped > 0)
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[DEV] pick {pick}/{pdfs.Count} (skip {skipped} invalid -> {selectedIndex + 1}) -> {selected}");
            }
            else if (fallbackBack)
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[DEV] pick {pick}/{pdfs.Count} (fallback -> {selectedIndex + 1}) -> {selected}");
            }
            else
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[DEV] pick {pick}/{pdfs.Count} -> {selected}");
            }

            return selected;
        }

        private static int? GetEnvPick()
        {
            var raw = Environment.GetEnvironmentVariable("OBJPDF_PICK");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (int.TryParse(raw.Trim(), out var value))
                return value;
            return null;
        }

        private static string? GetDevDir()
        {
            var devDir = Environment.GetEnvironmentVariable("OBJPDF_DEV_DIR");
            if (string.IsNullOrWhiteSpace(devDir))
                devDir = Environment.GetEnvironmentVariable("OBJ_FIND_INPUT_DIR");
            if (string.IsNullOrWhiteSpace(devDir))
                return null;
            return NormalizePathForCurrentOS(devDir);
        }

        private static bool IsDevEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("OBJPDF_DEV");
            return raw != null && raw.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || raw?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
        }

        private static bool IsDevRandom()
        {
            var raw = Environment.GetEnvironmentVariable("OBJPDF_RANDOM");
            return raw != null && raw.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
                || raw?.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) == true
                || raw?.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
        }

        public static string FormatPathForOutput(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";

            switch (_preferredOutputStyle)
            {
                case PathStyle.Wsl:
                    return ToWslPath(path);
                case PathStyle.Windows:
                    return ToWindowsPath(path);
                default:
                    return path;
            }
        }

        private static void TrackPreferredStyle(string value)
        {
            if (_preferredOutputStyle != PathStyle.Auto) return;
            if (string.IsNullOrWhiteSpace(value)) return;
            if (value.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
            {
                _preferredOutputStyle = PathStyle.Wsl;
                return;
            }
            if (Regex.IsMatch(value, @"^[A-Za-z]:[\\/].+"))
            {
                _preferredOutputStyle = PathStyle.Windows;
            }
        }

        private static string ToWslPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase)) return path;
            var m = Regex.Match(path, @"^(?<drive>[A-Za-z]):[\\/](?<rest>.*)$");
            if (m.Success)
            {
                var drive = m.Groups["drive"].Value.ToLowerInvariant();
                var rest = m.Groups["rest"].Value.Replace('\\', '/');
                return $"/mnt/{drive}/{rest}";
            }
            return path;
        }

        private static string ToWindowsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            if (Regex.IsMatch(path, @"^[A-Za-z]:[\\/].+")) return path;
            if (path.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && string.Equals(parts[0], "mnt", StringComparison.OrdinalIgnoreCase))
                {
                    var drive = parts[1];
                    if (drive.Length == 1)
                    {
                        var rest = string.Join("\\", parts.Skip(2));
                        return $"{drive.ToUpperInvariant()}:\\{rest}";
                    }
                }
            }
            return path;
        }

        private static bool LooksLikePath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase)) return true;
            if (Regex.IsMatch(value, @"^[A-Za-z]:[\\/].+")) return true;
            return false;
        }

        private static bool IsWindows()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }

        private static bool IsWSL()
        {
            return !IsWindows() &&
                   (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null ||
                    Environment.GetEnvironmentVariable("WSL_INTEROP") != null);
        }
    }
}
