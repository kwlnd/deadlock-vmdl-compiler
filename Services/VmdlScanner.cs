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
                var filenameStem = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
                var db = HeroDatabase.GetDatabase();

                // Heroes live in heroes_wip or heroes_staging. Other models, such as
                // neutral creeps, are listed only when their file name is a preset.
                if (!cleanPath.Contains("heroes_wip") && !cleanPath.Contains("heroes_staging") &&
                    !db.ContainsKey(filenameStem))
                    continue;

                // Skip accessory/fx files
                if (filenameStem.EndsWith("_dragon") || filenameStem.EndsWith("_horse") || filenameStem.EndsWith("_horse_knight") ||
                    filenameStem.EndsWith("_mace") || filenameStem.EndsWith("_gun") || filenameStem.EndsWith("_weapon") ||
                    filenameStem.EndsWith("_arms") || filenameStem.EndsWith("_fx") || filenameStem.EndsWith("_projectile") ||
                    filenameStem.EndsWith("_ref") || filenameStem.StartsWith("text_") || filenameStem.StartsWith("piece"))
                {
                    continue;
                }

                string? hero = null;

                if (db.ContainsKey(filenameStem))
                {
                    hero = filenameStem;
                }
                else
                {
                    foreach (var key in db.Keys)
                    {
                        if (filenameStem == key + "_body" || filenameStem == key + "_model" || filenameStem == key + "_base")
                        {
                            hero = key;
                            break;
                        }
                    }

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

                if (string.IsNullOrEmpty(hero))
                    continue;

                var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(fullPath, searchPath);

                // Clean display without any bracket prefixes
                results.Add(new DiscoveredModel
                {
                    Display = subpath,
                    Hero = hero,
                    FullPath = fullPath,
                    Addon = addonName,
                    Subpath = subpath,
                    Filename = file.Name
                });
            }
        }
        catch { }

        return results.OrderBy(m => m.Display).ToList();
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
