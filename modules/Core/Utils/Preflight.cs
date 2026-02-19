using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using iText.Kernel.Pdf;

namespace Obj.Utils
{
    public static class Preflight
    {
        private static readonly object Lock = new();
        private static HashSet<string> InvalidFiles = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsInvalid(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var full = SafeFullPath(path);
            lock (Lock)
                return InvalidFiles.Contains(full);
        }

        public static List<string> FilterInvalid(IEnumerable<string> inputs, string label = "input")
        {
            var list = inputs?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            if (list.Count == 0) return list;
            var valid = new List<string>(list.Count);
            var skipped = 0;
            foreach (var item in list)
            {
                if (IsInvalid(item))
                {
                    skipped++;
                    continue;
                }
                valid.Add(item);
            }
            if (skipped > 0)
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[PREFLIGHT] {label}: ignorados {skipped} arquivos invalidos");
            }
            return valid;
        }

        public static void Run(string[] args)
        {
            if (args == null || args.Length == 0)
                return;
            if (args.Any(a => a.Equals("--no-preflight", StringComparison.OrdinalIgnoreCase)))
                return;

            var defaults = LoadDefaults();
            if (!defaults.Enabled && !args.Any(a => a.Equals("--preflight", StringComparison.OrdinalIgnoreCase)))
                return;

            var inputs = ResolveInputs(args);
            if (inputs.Count == 0)
                return;

            var jobs = defaults.Jobs > 0 ? defaults.Jobs : 4;
            var timeout = defaults.TimeoutSec > 0 ? defaults.TimeoutSec : 20;
            var log = defaults.Log;

            var invalid = new ConcurrentBag<string>();
            var progress = ProgressReporter.FromConfig("preflight", inputs.Count);
            var opts = new ParallelOptions { MaxDegreeOfParallelism = jobs };
            Parallel.ForEach(inputs, opts, file =>
            {
                if (!CheckPdf(file, timeout, out var reason))
                {
                    invalid.Add(file);
                    if (log && !string.IsNullOrWhiteSpace(reason) && !ReturnUtils.IsEnabled())
                        Console.Error.WriteLine($"[PREFLIGHT] invalid: {Path.GetFileName(file)} ({reason})");
                }
                progress?.Tick(Path.GetFileName(file));
            });

            lock (Lock)
            {
                InvalidFiles = new HashSet<string>(
                    invalid.Select(SafeFullPath),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (InvalidFiles.Count > 0)
            {
                if (!ReturnUtils.IsEnabled())
                    Console.Error.WriteLine($"[PREFLIGHT] invalid_total={InvalidFiles.Count}");
            }
        }

        private static bool CheckPdf(string path, double timeoutSec, out string reason)
        {
            reason = "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                reason = "missing";
                return false;
            }
            if (!HasPdfHeader(path))
            {
                reason = "header";
                return false;
            }

            if (timeoutSec <= 0)
                return true;

            string? err = null;
            var task = Task.Run(() =>
            {
                try
                {
                    using var reader = new PdfReader(path);
                    reader.SetUnethicalReading(true);
                    using var doc = new PdfDocument(reader);
                    _ = doc.GetNumberOfPages();
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                }
            });

            if (!task.Wait(TimeSpan.FromSeconds(timeoutSec)))
            {
                reason = $"timeout>{timeoutSec:0.0}s";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(err))
            {
                reason = err!;
                return false;
            }
            return true;
        }

        private static bool HasPdfHeader(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 5)
                    return false;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Span<byte> header = stackalloc byte[5];
                var read = fs.Read(header);
                if (read < 5) return false;
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

        private static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? ""; }
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
            {
                foreach (var part in inputsList.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    inputs.AddRange(ExpandSingle(part.Trim()));
            }
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
            if (string.IsNullOrWhiteSpace(value))
                return new List<string>();
            if (File.Exists(value))
                return new List<string> { value };
            if (Directory.Exists(value))
                return Directory.GetFiles(value, "*.pdf")
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            return new List<string>();
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

        private static ExecutionConfig.PreflightDefaults LoadDefaults()
        {
            return ExecutionConfig.GetPreflightDefaults();
        }
    }
}
