using System.Reflection;
using System.Text.Json;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class HeroDatabase
{
    private static Dictionary<string, HeroPreset>? _database;
    public static string? ActiveCustomFilePath { get; private set; }
    public static string? CustomLoadError { get; private set; }

    public static int LoadCustomDatabase(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var data = ReadPresetFile(fullPath);
        // Validate the complete file before replacing the active list.
        _database = data;
        ActiveCustomFilePath = fullPath;
        CustomLoadError = null;
        return data.Count;
    }

    public static void UseBuiltInDatabase()
    {
        _database = LoadBuiltInDatabase();
        ActiveCustomFilePath = null;
        CustomLoadError = null;
    }

    public static Dictionary<string, HeroPreset> ReadPresetFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a JSON object containing named presets.");
        var data = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Preset '{entry.Name}' must be a named object.");
            var preset = entry.Value.Deserialize<HeroPreset>(options)!;
            preset.Skel = ValidateReference(preset.Skel, entry.Name, "skel");
            preset.Graph = ValidateReference(preset.Graph, entry.Name, "graph");
            preset.UiGraph = ValidateReference(preset.UiGraph, entry.Name, "ui_graph");
            var namedGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, graph) in preset.NamedGraphs ?? new())
            {
                if (string.IsNullOrWhiteSpace(name) || name.Equals("ui", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("default", StringComparison.OrdinalIgnoreCase) || name.Any(c => c == '"' || char.IsControl(c)))
                    throw new InvalidDataException($"Preset '{entry.Name}' has an invalid named graph identifier. Use graph/ui_graph for default and UI bindings.");
                var reference = ValidateReference(graph, entry.Name, $"named_graphs.{name}");
                if (reference.Length == 0 || !namedGraphs.TryAdd(name, reference))
                    throw new InvalidDataException($"Preset '{entry.Name}' has an empty or duplicate named graph '{name}'.");
            }
            preset.NamedGraphs = namedGraphs;
            if (preset.Skel.Length == 0 && preset.Graph.Length == 0 && preset.UiGraph.Length == 0 && namedGraphs.Count == 0)
                throw new InvalidDataException($"Preset '{entry.Name}' does not contain any AG2 paths.");
            if (!data.TryAdd(entry.Name, preset))
                throw new InvalidDataException($"Duplicate preset name '{entry.Name}'. Names are case-insensitive.");
        }
        if (data.Count == 0) throw new InvalidDataException("The preset list is empty.");
        return data;
    }

    private static string ValidateReference(string? path, string preset, string field)
    {
        var reference = path?.Trim().Replace('\\', '/') ?? string.Empty;
        if (reference.Any(c => c == '"' || char.IsControl(c)))
            throw new InvalidDataException($"Preset '{preset}' has an invalid {field} path.");
        return reference;
    }

    private static string GetUserDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeadlockVmdlCompiler", "hero_paths.json");

    public static Dictionary<string, HeroPreset> GetDatabase()
    {
        if (_database != null)
            return _database;

        _database = LoadDatabase();
        return _database;
    }

    public static IReadOnlyDictionary<string, HeroPreset> GetVisiblePresets()
    {
        var current = GetDatabase();
        if (ActiveCustomFilePath != null) return current;
        var builtIn = LoadBuiltInDatabase();
        if (builtIn.Count == 0)
            return GetDatabase();

        var visible = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, defaultPreset) in builtIn)
            visible[key] = current.TryGetValue(key, out var updated) && updated != null
                ? updated : defaultPreset;

        return visible;
    }

    private static Dictionary<string, HeroPreset> LoadDatabase(AppConfig? config = null)
    {
        ActiveCustomFilePath = null;
        CustomLoadError = null;
        config ??= ConfigManager.LoadConfig();
        if (config.UseBuiltInHeroPaths) return LoadBuiltInDatabase();
        var selectedFile = config.HeroPathsFile;
        if (!string.IsNullOrWhiteSpace(selectedFile))
        {
            try
            {
                var data = ReadPresetFile(selectedFile);
                ActiveCustomFilePath = Path.GetFullPath(selectedFile);
                return data;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                CustomLoadError = $"Could not load selected hero_paths.json: {ex.Message}";
            }
        }
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            GetUserDatabasePath(),
            Path.Combine(exeDir, "hero_paths.json"),
            Path.Combine(exeDir, "tools", "hero_paths.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "hero_paths.json")
        };

        foreach (var p in candidates)
        {
            if (File.Exists(p))
            {
                try
                {
                    var dict = ReadPresetFile(p);
                    CorrectLegacyPresets(dict);
                    return IncludeMissingBuiltInPresets(dict);
                }
                catch { }
            }
        }

        return LoadBuiltInDatabase();
    }

    private static Dictionary<string, HeroPreset> IncludeMissingBuiltInPresets(
        Dictionary<string, HeroPreset> data)
    {
        // New releases must remain available for auto-detection when an older
        // local database is installed. Keep every user's existing override.
        foreach (var (key, preset) in LoadBuiltInDatabase())
            data.TryAdd(key, preset);
        return data;
    }

    private static Dictionary<string, HeroPreset> LoadBuiltInDatabase()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "DeadlockVmdlCompiler.hero_paths.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                if (data != null)
                {
                    var dict = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in data)
                        dict[kv.Key] = kv.Value;
                    CorrectLegacyPresets(dict);
                    return dict;
                }
            }
        }
        catch { }

        return new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
    }

    public static void ReloadDatabase(AppConfig? config = null)
    {
        _database = LoadDatabase(config);
    }

    private static void CorrectLegacyPresets(Dictionary<string, HeroPreset> data)
    {
        if (data.TryGetValue("seven", out var seven) &&
            data.TryGetValue("gigawatt_prisoner", out var gigawatt) &&
            string.Equals(seven.Skel, "models/heroes_wip/frank/frank.vnmskel", StringComparison.OrdinalIgnoreCase) &&
            seven.Graph?.EndsWith("+frank.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(gigawatt.Skel, "models/heroes_staging/gigawatt_prisoner/gigawatt_prisoner.vnmskel", StringComparison.OrdinalIgnoreCase) &&
            gigawatt.Graph?.EndsWith("+gigawatt.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            gigawatt.UiGraph?.EndsWith("+gigawatt.vnmgraph", StringComparison.OrdinalIgnoreCase) == true)
        {
            data["seven"] = new HeroPreset
            {
                Skel = gigawatt.Skel,
                Graph = gigawatt.Graph,
                UiGraph = gigawatt.UiGraph
            };
        }

        if (data.TryGetValue("familiar", out var familiar) &&
            data.TryGetValue("familiar_wip", out var familiarWip) &&
            string.Equals(familiar.Skel, familiarWip.Skel, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(familiar.Graph, familiarWip.Graph, StringComparison.OrdinalIgnoreCase) &&
            familiarWip.UiGraph?.EndsWith("+familiar.vnmgraph", StringComparison.OrdinalIgnoreCase) == true &&
            familiar.UiGraph?.EndsWith("+frank.vnmgraph", StringComparison.OrdinalIgnoreCase) == true)
        {
            familiar.UiGraph = familiarWip.UiGraph;
        }
    }
}
