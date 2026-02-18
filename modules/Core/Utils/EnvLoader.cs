using System;
using System.Collections.Generic;
using System.IO;

namespace Obj.Utils
{
    public static class EnvLoader
    {
        public static void LoadDefaults()
        {
            var candidates = new List<string>();

            var explicitPath = Environment.GetEnvironmentVariable("OBJ_DOTENV");
            if (!string.IsNullOrWhiteSpace(explicitPath))
                candidates.Add(explicitPath);

            var cwd = Directory.GetCurrentDirectory();
            candidates.Add(Path.Combine(cwd, ".env"));

            var repoRoot = FindRepoRoot();
            if (!string.IsNullOrWhiteSpace(repoRoot))
                candidates.Add(Path.Combine(repoRoot, ".env"));

            foreach (var path in candidates)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    continue;
                LoadFile(path);
            }
        }

        private static void LoadFile(string path)
        {
            var allowOverwrite = string.Equals(Environment.GetEnvironmentVariable("OBJ_ENV_OVERWRITE"), "1", StringComparison.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    continue;
                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    line = line.Substring(7).Trim();

                var idx = line.IndexOf('=');
                if (idx <= 0)
                    continue;

                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();
                if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);
                else if (value.StartsWith("'") && value.EndsWith("'") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);

                if (!allowOverwrite && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    continue;

                Environment.SetEnvironmentVariable(key, value);
            }
        }

        private static string? FindRepoRoot()
        {
            var baseDir = PatternRegistry.ResolveBaseDir();
            var dir = new DirectoryInfo(baseDir);
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var probe = Path.Combine(dir.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                    return dir.FullName;
                dir = dir.Parent;
            }

            dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            for (int i = 0; i < 8 && dir != null; i++)
            {
                var probe = Path.Combine(dir.FullName, "modules", "PatternModules", "registry");
                if (Directory.Exists(probe))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return null;
        }
    }
}
