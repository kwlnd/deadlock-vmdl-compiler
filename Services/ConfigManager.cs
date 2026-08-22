using System;
using System.IO;
using System.Text.Json;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class ConfigManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetConfigPath()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(exeDir, "config.json");
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
                   norm.Contains("appdata\\local\\temp") ||
                   norm.Contains("hero_filter_test_") ||
                   norm.Contains("citadel_test_");
        }
        catch
        {
            return false;
        }
    }

    public static AppConfig LoadConfig()
    {
        var config = new AppConfig();
        var path = GetConfigPath();

        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                {
                    config = loaded;
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        // Sanitize temporary or deleted paths
        if (IsTemporaryPath(config.CitadelAddonsDir) || (!string.IsNullOrEmpty(config.CitadelAddonsDir) && !Directory.Exists(config.CitadelAddonsDir)))
        {
            config.CitadelAddonsDir = string.Empty;
        }

        if (!string.IsNullOrEmpty(config.CsWinDir) && !Directory.Exists(config.CsWinDir))
        {
            config.CsWinDir = string.Empty;
        }

        if (IsTemporaryPath(config.LastTargetPath))
        {
            config.LastTargetPath = string.Empty;
        }

        return config;
    }

    public static bool SaveConfig(AppConfig config)
    {
        try
        {
            if (IsTemporaryPath(config.CitadelAddonsDir))
                config.CitadelAddonsDir = string.Empty;

            if (IsTemporaryPath(config.LastTargetPath))
                config.LastTargetPath = string.Empty;

            var path = GetConfigPath();
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
