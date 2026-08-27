using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class HeroDatabase
{
    private static Dictionary<string, HeroPreset>? _database;

    public static Dictionary<string, HeroPreset> GetDatabase()
    {
        if (_database != null)
            return _database;

        _database = LoadDatabase();
        return _database;
    }

    private static Dictionary<string, HeroPreset> LoadDatabase()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
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
                    var json = File.ReadAllText(p);
                    var data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                    if (data != null)
                    {
                        var dict = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in data)
                            dict[kv.Key] = kv.Value;
                        return dict;
                    }
                }
                catch { }
            }
        }

        // Try embedded resource
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
                    return dict;
                }
            }
        }
        catch { }

        return new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);
    }

    public static string SaveDatabase(Dictionary<string, HeroPreset> data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hero_paths.json");

        try
        {
            File.WriteAllText(targetFile, json);
        }
        catch { }

        try
        {
            var curDirFile = Path.Combine(Directory.GetCurrentDirectory(), "hero_paths.json");
            if (!string.Equals(targetFile, curDirFile, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(curDirFile, json);
            }
        }
        catch { }

        _database = new Dictionary<string, HeroPreset>(data, StringComparer.OrdinalIgnoreCase);
        return targetFile;
    }

    public static void ReloadDatabase()
    {
        _database = LoadDatabase();
    }

    public static (bool Success, string Message, int Count) RestoreOriginalDatabase()
    {
        try
        {
            Dictionary<string, HeroPreset>? data = null;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "DeadlockVmdlCompiler.hero_paths.json";
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
            }

            if (data == null)
            {
                var candidates = new[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hero_paths.json"),
                    Path.Combine(Directory.GetCurrentDirectory(), "hero_paths.json")
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        var json = File.ReadAllText(c);
                        data = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(json);
                        if (data != null) break;
                    }
                }
            }

            if (data == null || data.Count == 0)
            {
                return (false, "Could not find valid hero preset database.", 0);
            }

            var dict = new Dictionary<string, HeroPreset>(data, StringComparer.OrdinalIgnoreCase);
            _database = dict;
            SaveDatabase(dict);

            return (true, $"Restored {dict.Count} default hero presets.", dict.Count);
        }
        catch (Exception ex)
        {
            return (false, $"Error restoring presets: {ex.Message}", 0);
        }
    }
}
