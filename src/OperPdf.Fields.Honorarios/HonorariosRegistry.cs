using System.Text.Json;

namespace OperPdf.Fields.Honorarios;

public sealed class HonorariosRegistry
{
    public sealed record AliasRule(string Contains, string Especialidade);
    public sealed record EspecieRule(string Contains, string Especie);

    private sealed class AliasFile
    {
        public List<AliasRule> Aliases { get; set; } = new();
    }

    private sealed class EspecieFile
    {
        public List<EspecieRule> EspecieKeywords { get; set; } = new();
    }

    public IReadOnlyList<AliasRule> Aliases { get; init; } = Array.Empty<AliasRule>();
    public IReadOnlyList<EspecieRule> EspecieKeywords { get; init; } = Array.Empty<EspecieRule>();

    public static HonorariosRegistry Load(string? registryRoot = null)
    {
        var root = ResolveRegistryRoot(registryRoot);
        var honorariosDir = Path.Combine(root, "honorarios");

        var aliases = LoadAliases(Path.Combine(honorariosDir, "especialidade_aliases.json"));
        var especieKeywords = LoadEspecieKeywords(Path.Combine(honorariosDir, "especie_keywords.json"));

        return new HonorariosRegistry
        {
            Aliases = aliases,
            EspecieKeywords = especieKeywords
        };
    }

    private static IReadOnlyList<AliasRule> LoadAliases(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<AliasRule>();

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AliasFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed?.Aliases
                ?.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Contains) && !string.IsNullOrWhiteSpace(x.Especialidade))
                .Select(x => new AliasRule(x.Contains.Trim(), x.Especialidade.Trim()))
                .ToArray() ?? Array.Empty<AliasRule>();
        }
        catch
        {
            return Array.Empty<AliasRule>();
        }
    }

    private static IReadOnlyList<EspecieRule> LoadEspecieKeywords(string path)
    {
        if (!File.Exists(path))
            return Array.Empty<EspecieRule>();

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<EspecieFile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed?.EspecieKeywords
                ?.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Contains) && !string.IsNullOrWhiteSpace(x.Especie))
                .Select(x => new EspecieRule(x.Contains.Trim(), x.Especie.Trim()))
                .ToArray() ?? Array.Empty<EspecieRule>();
        }
        catch
        {
            return Array.Empty<EspecieRule>();
        }
    }

    public static string ResolveRegistryRoot(string? root = null)
    {
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            return Path.GetFullPath(root);

        var cwd = Directory.GetCurrentDirectory();
        var appBase = AppContext.BaseDirectory;

        var candidates = new[]
        {
            Path.Combine(cwd, "registry"),
            Path.Combine(cwd, "..", "registry"),
            Path.Combine(cwd, "..", "..", "registry"),
            Path.Combine(cwd, "..", "..", "..", "registry"),
            Path.Combine(appBase, "registry"),
            Path.Combine(appBase, "..", "registry"),
            Path.Combine(appBase, "..", "..", "registry"),
            Path.Combine(appBase, "..", "..", "..", "registry")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full))
                return full;
        }

        return Path.GetFullPath(Path.Combine(cwd, "registry"));
    }
}
