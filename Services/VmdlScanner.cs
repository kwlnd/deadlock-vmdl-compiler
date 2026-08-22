using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class VmdlScanner
{
    public static List<DiscoveredModel> ScanHeroModels(string searchPath)
    {
        var results = new List<DiscoveredModel>();
        if (string.IsNullOrWhiteSpace(searchPath) || !Directory.Exists(searchPath))
            return results;

        try
        {
            var dirInfo = new DirectoryInfo(searchPath);
            var files = dirInfo.EnumerateFiles("*.vmdl", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                var fullPath = file.FullName;
                var cleanPath = fullPath.Replace('\\', '/').ToLowerInvariant();

                // Must be strictly inside heroes_wip or heroes_staging
                if (!cleanPath.Contains("heroes_wip") && !cleanPath.Contains("heroes_staging"))
                    continue;

                var filenameStem = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();

                // Skip known accessory, summon, weapon, and fx suffix files
                if (filenameStem.EndsWith("_dragon") || filenameStem.EndsWith("_horse") || filenameStem.EndsWith("_horse_knight") ||
                    filenameStem.EndsWith("_mace") || filenameStem.EndsWith("_gun") || filenameStem.EndsWith("_weapon") ||
                    filenameStem.EndsWith("_arms") || filenameStem.EndsWith("_fx") || filenameStem.EndsWith("_projectile") ||
                    filenameStem.EndsWith("_ref") || filenameStem.StartsWith("text_") || filenameStem.StartsWith("piece"))
                {
                    continue;
                }

                var db = HeroDatabase.GetDatabase();
                string? hero = null;

                // 1. Direct match with hero database key (e.g. "bookworm.vmdl" -> "bookworm")
                if (db.ContainsKey(filenameStem))
                {
                    hero = filenameStem;
                }
                else
                {
                    // 2. Check canonical hero body/model names (e.g. "<hero>_body", "<hero>_model", "<hero>_base")
                    foreach (var key in db.Keys)
                    {
                        if (filenameStem == key + "_body" || filenameStem == key + "_model" || filenameStem == key + "_base")
                        {
                            hero = key;
                            break;
                        }
                    }

                    // 3. Check parent directory name (e.g. folder "hornet_v3" -> "hornet", "bookworm" -> "bookworm")
                    if (string.IsNullOrEmpty(hero))
                    {
                        var parentDir = file.Directory?.Name.ToLowerInvariant() ?? string.Empty;
                        foreach (var key in db.Keys)
                        {
                            if (parentDir == key || parentDir.StartsWith(key + "_") || parentDir.StartsWith(key + "v") || parentDir.Contains(key))
                            {
                                hero = key;
                                break;
                            }
                        }
                    }
                }

                // If filename/folder does not match a known hero, skip it
                if (string.IsNullOrEmpty(hero))
                    continue;

                var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(fullPath, searchPath);
                var display = (!string.IsNullOrEmpty(addonName) && addonName != "addon")
                    ? $"[{addonName}] {subpath} ({hero})"
                    : $"{subpath} ({hero})";

                results.Add(new DiscoveredModel
                {
                    Display = display,
                    Hero = hero,
                    FullPath = fullPath,
                    Addon = addonName,
                    Subpath = subpath,
                    Filename = file.Name
                });
            }
        }
        catch { }

        return results.OrderBy(m => m.Hero).ThenBy(m => m.Display).ToList();
    }

    public static List<DiscoveredModel> ScanHeroModelsInAddon(string vmdlOrAddonPath, string? citadelAddonsDir = null)
    {
        var results = new List<DiscoveredModel>();
        try
        {
            var clean = vmdlOrAddonPath.Replace('\\', '/');
            var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(vmdlOrAddonPath, citadelAddonsDir);
            string? addonRoot = null;

            if (!string.IsNullOrEmpty(addonName) && addonName != "addon")
            {
                var matchIdx = clean.IndexOf("/" + addonName + "/", StringComparison.OrdinalIgnoreCase);
                if (matchIdx >= 0)
                {
                    addonRoot = clean.Substring(0, matchIdx + addonName.Length + 1);
                }
                else if (!string.IsNullOrEmpty(citadelAddonsDir))
                {
                    var candidate = Path.Combine(citadelAddonsDir, addonName);
                    if (Directory.Exists(candidate))
                        addonRoot = candidate;
                }
            }

            if (string.IsNullOrEmpty(addonRoot))
            {
                if (Directory.Exists(vmdlOrAddonPath))
                    addonRoot = vmdlOrAddonPath;
                else if (File.Exists(vmdlOrAddonPath))
                    addonRoot = Path.GetDirectoryName(vmdlOrAddonPath);
            }

            if (!string.IsNullOrEmpty(addonRoot) && Directory.Exists(addonRoot))
            {
                var models = ScanHeroModels(addonRoot);
                if (models.Count > 0)
                    return models;
            }
        }
        catch { }

        return results;
    }

    public static List<DiscoveredAddon> ScanAddons(string citadelAddonsDir)
    {
        var results = new List<DiscoveredAddon>();
        if (string.IsNullOrWhiteSpace(citadelAddonsDir) || !Directory.Exists(citadelAddonsDir))
            return results;

        try
        {
            var dirInfo = new DirectoryInfo(citadelAddonsDir);
            var subdirs = dirInfo.EnumerateDirectories();

            foreach (var dir in subdirs)
            {
                if (dir.Name.StartsWith(".") || dir.Name.StartsWith("_"))
                    continue;

                var addonName = dir.Name;
                var heroModels = ScanHeroModels(dir.FullName);

                results.Add(new DiscoveredAddon
                {
                    Name = addonName,
                    FullPath = dir.FullName,
                    HeroModels = heroModels,
                    Display = addonName
                });
            }
        }
        catch { }

        return results.OrderByDescending(a => a.HasHero).ThenBy(a => a.Name).ToList();
    }
}
