using System.Text.RegularExpressions;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Paths inside CSDK12 addons: which addon and hero a model belongs to, and its game-side folders.</summary>
public static class CsdkPaths
{
    public static string? DetectHeroFromPath(string filepath)
    {
        var db = HeroDatabase.GetDatabase();
        var clean = filepath.Replace('\\', '/').ToLowerInvariant();
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        // Variant models can share a parent folder and skeleton with the base model.
        var filename = Path.GetFileNameWithoutExtension(filepath).ToLowerInvariant();
        if (db.ContainsKey(filename)) return filename;

        // Older game folders and local databases can use names removed from the
        // curated preset menu. Resolve them to the remaining preset first.
        var renamedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadman_danny"] = "deadpack",
            ["familiar"] = "familiar_wip",
            ["ghost"] = "geist",
            ["gigawatt_prisoner"] = "seven",
            ["hornet"] = "vindicta",
            ["inferno"] = "infernus",
            ["lady_geist"] = "geist",
            ["nano"] = "calico",
            ["nurse_harrow"] = "nurse",
            ["rat_king"] = "ratking",
            ["solomon"] = "chessmaster",
            ["synth"] = "pocket",
            ["tengu"] = "ivy",
            ["violet"] = "artist"
        };

        // Check parent folder names from closest upwards
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var folder = parts[i];
            var renamed = renamedFolders
                .Where(pair => folder.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                               folder.StartsWith(pair.Key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            if (renamed.Value != null && db.ContainsKey(renamed.Value))
                return renamed.Value;
            if (db.ContainsKey(folder))
                return folder;
            var versionedMatch = db.Keys
                .Where(key => folder.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(key => key.Length)
                .FirstOrDefault();
            if (versionedMatch != null)
                return versionedMatch;
        }

        return null;
    }

    public static (string Container, string AddonName, string Subpath) ParseCsdkPath(string csdkPath, string? citadelAddonsDir = null)
    {
        var clean = csdkPath.Replace('\\', '/');

        // 1. Standard pattern: .../content/(citadel_addons|citadel_community_addons|citadel)/<addon_name>/<subpath>
        var m = Regex.Match(clean, @"content/(citadel_addons|citadel_community_addons|citadel)/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

        // 2. General content subfolder pattern: .../content/<addon_name>/<subpath>
        var m2 = Regex.Match(clean, @"content/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m2.Success)
            return ("citadel_addons", m2.Groups[1].Value, m2.Groups[2].Value);

        // 3. Relative to configured citadelAddonsDir
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var cleanAddons = citadelAddonsDir.Replace('\\', '/').TrimEnd('/');
            if (clean.StartsWith(cleanAddons + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = clean[(cleanAddons.Length + 1)..];
                var parts = rel.Split('/', 2);
                if (parts.Length == 2)
                    return ("citadel_addons", parts[0], parts[1]);
                return ("citadel_addons", "addon", parts[0]);
            }
        }

        return ("citadel_addons", "addon", Path.GetFileName(clean));
    }

    private static string? FindCsdkRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var clean = path.Replace('\\', '/');
        var standard = Regex.Match(clean, @"^(.*?)/content/(citadel_addons|citadel_community_addons|citadel)(/|$)",
            RegexOptions.IgnoreCase);
        if (standard.Success) return standard.Groups[1].Value;
        var index = clean.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? clean[..index] : null;
    }

    /// <summary>The one place that decides where an addon's compiled files live; deploy and packaging share it.</summary>

    /// <summary>The one place that decides where an addon's compiled files live; deploy and packaging share it.</summary>
    public static string ResolveGameAddonDir(string targetVmdlPath, string? citadelAddonsDir, string addonName)
    {
        var (container, _, _) = ParseCsdkPath(targetVmdlPath, citadelAddonsDir);
        var root = FindCsdkRoot(targetVmdlPath) ?? FindCsdkRoot(citadelAddonsDir);
        if (root != null)
            return Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", container, addonName);

        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var parent = Directory.GetParent(citadelAddonsDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "game", "citadel_addons", addonName);
        }

        return Path.Combine(Path.GetDirectoryName(targetVmdlPath) ?? string.Empty, "game", addonName);
    }

    public static (string Skel, string Graph, string UiGraph) DeriveDefaultPaths(string vmdlPath)
    {
        var db = HeroDatabase.GetDatabase();
        var hero = DetectHeroFromPath(vmdlPath);

        if (hero != null && db.TryGetValue(hero, out var preset))
        {
            return (preset.Skel, preset.Graph, preset.UiGraph);
        }

        // There is no reliable way to infer compiled Deadlock references from a custom VMDL path.
        return (string.Empty, string.Empty, string.Empty);
    }
}
