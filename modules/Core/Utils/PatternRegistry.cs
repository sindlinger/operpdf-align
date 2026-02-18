using System;
using System.Collections.Generic;
using System.IO;

namespace Obj.Utils
{
    public static class PatternRegistry
    {
        private static string? _resolvedBase;

        public static string ResolveBaseDir()
        {
            if (!string.IsNullOrWhiteSpace(_resolvedBase))
                return _resolvedBase!;

            var cwd = Directory.GetCurrentDirectory();
            var exeBase = AppContext.BaseDirectory;
            var candidates = new List<string>
            {
                Path.Combine(cwd, "modules", "PatternModules", "registry"),
                Path.Combine(cwd, "PatternModules", "registry"),
                Path.Combine(cwd, "..", "modules", "PatternModules", "registry"),
                Path.Combine(cwd, "..", "..", "modules", "PatternModules", "registry"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../modules/PatternModules/registry"))
            };

            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir))
                {
                    _resolvedBase = dir;
                    return dir;
                }
            }

            var fromCwd = new DirectoryInfo(cwd);
            for (int i = 0; i < 8 && fromCwd != null; i++)
            {
                var probe = Path.Combine(fromCwd.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                {
                    _resolvedBase = probe;
                    return probe;
                }
                fromCwd = fromCwd.Parent;
            }

            var fromExe = new DirectoryInfo(exeBase);
            for (int i = 0; i < 8 && fromExe != null; i++)
            {
                var probe = Path.Combine(fromExe.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                {
                    _resolvedBase = probe;
                    return probe;
                }
                fromExe = fromExe.Parent;
            }

            _resolvedBase = candidates[0];
            return _resolvedBase!;
        }

        public static string ResolvePath(params string[] parts)
        {
            var baseDir = ResolveBaseDir();
            var all = new List<string> { baseDir };
            all.AddRange(parts);
            return Path.Combine(all.ToArray());
        }

        public static string FindFile(params string[] parts)
        {
            var path = ResolvePath(parts);
            return File.Exists(path) ? path : "";
        }

        public static string FindDir(params string[] parts)
        {
            var path = ResolvePath(parts);
            return Directory.Exists(path) ? path : "";
        }

        public static IEnumerable<string> EnumerateFiles(string category, string pattern = "*.*")
        {
            var dir = ResolvePath(category);
            if (!Directory.Exists(dir))
                return Array.Empty<string>();
            return Directory.GetFiles(dir, pattern);
        }
    }
}
