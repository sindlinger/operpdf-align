using System;
using System.IO;
using System.Linq;
using Obj.TjpbDespachoExtractor.Config;
using Obj.TjpbDespachoExtractor.Reference;

namespace Obj.ValidatorModule
{
    public static class ValidatorContext
    {
        private static readonly object Sync = new();
        private static bool _catalogLoaded;
        private static string _catalogConfigPath = "";
        private static PeritoCatalog? _catalog;

        public static PeritoCatalog? GetPeritoCatalog(string? configPath = null)
        {
            var resolvedPath = ResolveConfigPath(configPath);

            lock (Sync)
            {
                if (_catalogLoaded && string.Equals(_catalogConfigPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                    return _catalog;

                _catalogLoaded = true;
                _catalogConfigPath = resolvedPath;
                _catalog = null;

                if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                    return _catalog;

                try
                {
                    var cfg = TjpbDespachoConfig.Load(resolvedPath);
                    _catalog = PeritoCatalog.Load(cfg.BaseDir, cfg.Reference.PeritosCatalogPaths);
                }
                catch
                {
                    _catalog = null;
                }

                return _catalog;
            }
        }

        public static string ResolveConfigPath(string? explicitConfigPath = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitConfigPath) && File.Exists(explicitConfigPath))
                return Path.GetFullPath(explicitConfigPath);

            var cwd = Directory.GetCurrentDirectory();
            var candidates = new[]
            {
                Path.Combine(cwd, "configs", "config.yaml"),
                Path.Combine(cwd, "configs", "config.yml"),
                Path.Combine(cwd, "OBJ", "configs", "config.yaml"),
                Path.Combine(cwd, "..", "configs", "config.yaml")
            };

            var path = candidates.FirstOrDefault(File.Exists) ?? "";
            return string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path);
        }

        public static void Reset()
        {
            lock (Sync)
            {
                _catalogLoaded = false;
                _catalogConfigPath = "";
                _catalog = null;
            }
        }
    }
}
