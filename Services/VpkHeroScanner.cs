using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

namespace DeadlockVmdlCompiler.Services;

public static class VpkHeroScanner
{
    public static async Task<(bool Success, string Message, Dictionary<string, HeroPreset> Presets)> ScanVpkForHeroesAsync(
        string targetPath,
        IProgress<(int Current, int Total, string CurrentModel)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = ResolveVpkPath(targetPath);

        var results = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            try
            {
                if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                {
                    using var archive = VpkArchive.Open(resolvedPath);
                    var targetPaths = archive.FindFiles("vmdl_c", directory =>
                        directory.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase) ||
                        directory.StartsWith("models/characters", StringComparison.OrdinalIgnoreCase));

                    if (targetPaths.Count == 0)
                    {
                        return (false, $"No hero models found in: {Path.GetFileName(resolvedPath)}", results);
                    }

                    int total = targetPaths.Count;
                    int current = 0;

                    foreach (var modelPath in targetPaths)
                    {
                        current++;
                        var fileName = Path.GetFileNameWithoutExtension(modelPath);
                        progress?.Report((current, total, fileName));

                        try
                        {
                            var byteData = archive.Read(modelPath);
                            if (byteData == null || byteData.Length == 0) continue;

                            using var res = new Resource();
                            using var ms = new MemoryStream(byteData);
                            res.Read(ms);

                            var directory = Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty;
                            var (hasAg2, preset, heroKey) = ExtractAg2FromResource(res, directory, fileName);
                            if (hasAg2 && preset != null && !string.IsNullOrEmpty(heroKey))
                            {
                                results[heroKey] = preset;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"skipped {modelPath} while scanning heroes: {ex.Message}");
                        }
                    }
                }
                else if (Directory.Exists(resolvedPath))
                {
                    var allVmdlcFiles = Directory.GetFiles(resolvedPath, "*.vmdl_c", SearchOption.AllDirectories)
                        .Where(f => f.Replace('\\', '/').Contains("/heroes", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    int total = allVmdlcFiles.Count;
                    int current = 0;

                    foreach (var filePath in allVmdlcFiles)
                    {
                        current++;
                        var fn = Path.GetFileNameWithoutExtension(filePath);
                        progress?.Report((current, total, fn));

                        try
                        {
                            using var res = new Resource();
                            res.Read(filePath);

                            var relPath = Path.GetRelativePath(resolvedPath, filePath).Replace('\\', '/');
                            var dirName = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? string.Empty;
                            var fileName = Path.GetFileNameWithoutExtension(filePath);
                            if (fileName.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                                fileName = Path.GetFileNameWithoutExtension(fileName);

                            var (hasAg2, preset, heroKey) = ExtractAg2FromResource(res, dirName, fileName);
                            if (hasAg2 && preset != null && !string.IsNullOrEmpty(heroKey))
                            {
                                results[heroKey] = preset;
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn($"skipped {filePath} while scanning heroes: {ex.Message}");
                        }
                    }
                }
                else
                {
                    return (false, $"File or folder not found: {resolvedPath}", results);
                }

                if (results.Count == 0)
                {
                    return (false, "Scan completed, but no models with AnimGraph2 nodes were found in the selected VPK.", results);
                }

                var merged = new Dictionary<string, HeroPreset>(HeroDatabase.GetDatabase(), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in results)
                {
                    merged[kv.Key] = kv.Value;
                }

                HeroDatabase.SaveDatabase(merged);

                return (true, $"Updated {merged.Count} hero preset(s).", merged);
            }
            catch (Exception ex)
            {
                return (false, $"Error scanning VPK: {ex.Message}", results);
            }
        });
    }

    public static async Task<(bool Success, string Message, List<NpcPreset> Presets)> ScanVpkForNpcsAsync(
        string targetPath,
        IProgress<(int Current, int Total, string CurrentModel)>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = ResolveVpkPath(targetPath);
        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(resolvedPath) ||
                    !resolvedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                    return (false, $"File or folder not found: {resolvedPath}", new List<NpcPreset>());

                using var archive = VpkArchive.Open(resolvedPath);
                // The game's unit list names the models it spawns (both teams' troopers, the
                // walker, guardian...); unused leftovers in models/npc stay out. A VPK without
                // it falls back to every model under models/npc.
                var modelPaths = NpcUnitModels(archive) ?? archive.FindFiles("vmdl_c", directory =>
                    directory.StartsWith("models/npc/", StringComparison.OrdinalIgnoreCase));
                if (modelPaths.Count == 0)
                    return (false, "No NPC models were found in the selected VPK.", new List<NpcPreset>());

                var results = new Dictionary<string, NpcPreset>(StringComparer.OrdinalIgnoreCase);
                var total = modelPaths.Count;
                var current = 0;
                foreach (var modelPath in modelPaths)
                {
                    current++;
                    progress?.Report((current, total, modelPath));

                    try
                    {
                        var bytes = archive.Read(modelPath);
                        if (bytes == null || bytes.Length == 0) continue;

                        using var resource = new Resource();
                        using var stream = new MemoryStream(bytes);
                        resource.Read(stream);

                        var (hasAg2, preset, _) = ExtractAg2FromResource(resource,
                            Path.GetDirectoryName(modelPath)?.Replace('\\', '/') ?? string.Empty,
                            Path.GetFileNameWithoutExtension(modelPath));
                        if (!hasAg2 || preset == null) continue;

                        var display = modelPath.StartsWith("models/npc/", StringComparison.OrdinalIgnoreCase)
                            ? modelPath["models/npc/".Length..]
                            : modelPath;
                        var key = modelPath[..^("_c".Length)];
                        results[modelPath] = new NpcPreset(
                            key,
                            display,
                            modelPath,
                            preset,
                            preset.NamedGraphs.ContainsKey("Neutrals"));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"skipped {modelPath} while scanning NPCs: {ex.Message}");
                    }
                }

                var list = results.Values
                    .OrderBy(preset => preset.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return list.Count == 0
                    ? (false, "NPC scan completed, but no models with AnimGraph2 nodes were found.", list)
                    : (true, $"Found {list.Count} NPC preset(s).", list);
            }
            catch (Exception ex)
            {
                return (false, $"Error scanning NPC presets: {ex.Message}", new List<NpcPreset>());
            }
        });
    }

    /// <summary>
    /// The NPC and neutral models scripts/npc_units.vdata spawns, as compiled VPK paths, or null
    /// when the VPK has no unit list.
    /// </summary>
    private static IReadOnlyList<string>? NpcUnitModels(VpkArchive archive)
    {
        var bytes = archive.Read("scripts/npc_units.vdata_c");
        if (bytes == null) return null;
        using var resource = new Resource();
        resource.Read(new MemoryStream(bytes));
        var text = resource.DataBlock?.ToString() ?? string.Empty;
        return Regex.Matches(text, @"resource_name:""((?:models/npc/|models/npc_units/)[^""]+\.vmdl)""")
            .Select(match => match.Groups[1].Value.Replace('\\', '/') + "_c")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(archive.Contains)
            .ToList();
    }

    private static string ResolveVpkPath(string targetPath)
    {
        var resolvedPath = Path.GetFullPath(targetPath);
        if (!Directory.Exists(resolvedPath)) return resolvedPath;

        var candidates = new[]
        {
            Path.Combine(resolvedPath, "pak01_dir.vpk"),
            Path.Combine(resolvedPath, "game", "citadel", "pak01_dir.vpk"),
            Path.Combine(resolvedPath, "citadel", "pak01_dir.vpk")
        };
        return candidates.FirstOrDefault(File.Exists) ?? resolvedPath;
    }

    private static (bool HasAg2, HeroPreset? Preset, string HeroKey) ExtractAg2FromResource(
        Resource resource,
        string dirName,
        string fileName)
    {
        try
        {
            if (resource.DataBlock == null)
                return (false, null, string.Empty);

            var text = resource.DataBlock.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return (false, null, string.Empty);

            if (!text.Contains("m_animGraph2Refs", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("m_vecNmSkeletonRefs", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, string.Empty);
            }

            string skel = string.Empty;
            string graph = string.Empty;
            string uiGraph = string.Empty;
            var namedGraphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var skelMatch = Regex.Match(text, @"m_vecNmSkeletonRefs\s*=\s*\[[\s\S]*?(?:resource:)?\s*""([^""]+\.vnmskel)""", RegexOptions.IgnoreCase);
            if (skelMatch.Success)
            {
                skel = skelMatch.Groups[1].Value.Trim();
            }

            var ag2SectionMatch = Regex.Match(text, @"m_animGraph2Refs\s*=\s*\[([\s\S]*?)\]\s*(?:\r?\n|$)", RegexOptions.IgnoreCase);
            if (ag2SectionMatch.Success)
            {
                var ag2Text = ag2SectionMatch.Groups[1].Value;
                var itemMatches = Regex.Matches(ag2Text, @"\{[\s\S]*?\}");
                foreach (Match item in itemMatches)
                {
                    var itemStr = item.Value;
                    var idMatch = Regex.Match(itemStr, @"m_sIdentifier\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
                    var graphMatch = Regex.Match(itemStr, @"m_hGraph\s*=\s*(?:resource:)?\s*""([^""]+\.vnmgraph)""", RegexOptions.IgnoreCase);

                    if (graphMatch.Success)
                    {
                        var id = idMatch.Success ? idMatch.Groups[1].Value.Trim() : string.Empty;
                        var gPath = graphMatch.Groups[1].Value.Trim();

                        if (string.IsNullOrEmpty(id) || id.Equals("default", StringComparison.OrdinalIgnoreCase))
                        {
                            graph = gPath;
                        }
                        else if (id.Equals("ui", StringComparison.OrdinalIgnoreCase))
                        {
                            uiGraph = gPath;
                        }
                        else
                        {
                            namedGraphs[id] = gPath;
                        }
                    }
                }
            }

            if (skel.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                skel = skel["resource:".Length..].Trim().Trim('"');
            if (graph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                graph = graph["resource:".Length..].Trim().Trim('"');
            if (uiGraph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                uiGraph = uiGraph["resource:".Length..].Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(skel) ||
                (string.IsNullOrWhiteSpace(graph) && namedGraphs.Count == 0))
                return (false, null, string.Empty);

            var heroKey = fileName.ToLowerInvariant();
            if (heroKey.EndsWith(".vmdl")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_body")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_model")) heroKey = heroKey[..^6];
            if (heroKey.EndsWith("_base")) heroKey = heroKey[..^5];

            var preset = new HeroPreset
            {
                Skel = skel,
                Graph = graph,
                UiGraph = uiGraph,
                NamedGraphs = namedGraphs
            };

            return (true, preset, heroKey);
        }
        catch
        {
            return (false, null, string.Empty);
        }
    }
}
