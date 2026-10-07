using System.Text.Json;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetConfigPath()
    {
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(userDir, "DeadlockVmdlCompiler", "config.json");
    }

    public static bool IsTemporaryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var norm = Path.GetFullPath(path).ToLowerInvariant();
            var tempDir = Path.GetTempPath().ToLowerInvariant().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return norm.StartsWith(tempDir) ||
                   norm.Contains("appdata\\local\\temp");
        }
        catch
        {
            return false;
        }
    }

    public static AppConfig LoadConfig()
    {
        var config = new AppConfig();
        var candidates = new[]
        {
            GetConfigPath(),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    config = loaded;
                    break;
                }
            }
            catch
            {
                // Try the legacy location if the user config is unreadable.
            }
        }

        // A missing folder is kept: the drive may simply be disconnected right now.
        if (IsTemporaryPath(config.CitadelAddonsDir))
            config.CitadelAddonsDir = string.Empty;

        return config;
    }

    public static bool SaveConfig(AppConfig config)
    {
        try
        {
            if (IsTemporaryPath(config.CitadelAddonsDir))
                config.CitadelAddonsDir = string.Empty;

            var path = GetConfigPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
