using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

namespace DeadlockVmdlCompiler.Services;

public class VpkEntry
{
    public string Extension { get; set; } = string.Empty;
    public string Directory { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public uint CRC32 { get; set; }
    public ushort PreloadBytes { get; set; }
    public ushort ArchiveIndex { get; set; }
    public uint EntryOffset { get; set; }
    public uint EntryLength { get; set; }
    public byte[]? PreloadData { get; set; }
}

public static class VpkHeroScanner
{
    /// <summary>
    /// Scans a pak01_dir.vpk (or directory with models) ONLY for models in models/heroes_staging and models/heroes_wip,
    /// extracting ONLY models that contain AG2 nodes (m_animGraph2Refs / m_vecNmSkeletonRefs).
    /// </summary>
    public static async Task<(bool Success, string Message, Dictionary<string, HeroPreset> Presets)> ScanVpkForHeroesAsync(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = Path.GetFullPath(targetPath);

        // If user selected a directory, find pak01_dir.vpk inside if available
        if (Directory.Exists(resolvedPath))
        {
            var candVpk = Path.Combine(resolvedPath, "pak01_dir.vpk");
            if (File.Exists(candVpk))
            {
                resolvedPath = candVpk;
            }
            else
            {
                var candCitadelVpk = Path.Combine(resolvedPath, "game", "citadel", "pak01_dir.vpk");
                if (File.Exists(candCitadelVpk))
                {
                    resolvedPath = candCitadelVpk;
                }
            }
        }

        var results = new Dictionary<string, HeroPreset>(StringComparer.OrdinalIgnoreCase);

        return await Task.Run(() =>
        {
            try
            {
                if (File.Exists(resolvedPath) && resolvedPath.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase))
                {
                    var entries = ReadVpkDirectory(resolvedPath);

                    // Filter ONLY models/heroes_staging and models/heroes_wip with extension vmdl_c
                    var targetEntries = entries.Where(e =>
                        e.Extension.Equals("vmdl_c", StringComparison.OrdinalIgnoreCase) &&
                        (e.Directory.StartsWith("models/heroes_staging", StringComparison.OrdinalIgnoreCase) ||
                         e.Directory.StartsWith("models/heroes_wip", StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (targetEntries.Count == 0)
                    {
                        return (false, $"No hero models found in models/heroes_staging or models/heroes_wip in: {Path.GetFileName(resolvedPath)}", results);
                    }

                    var vpkBaseDir = Path.GetDirectoryName(resolvedPath) ?? string.Empty;
                    var vpkBaseName = Path.GetFileNameWithoutExtension(resolvedPath);
                    if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
                    {
                        vpkBaseName = vpkBaseName[..^4];
                    }

                    foreach (var entry in targetEntries)
                    {
                        try
                        {
                            var byteData = ExtractVpkEntryBytes(entry, resolvedPath, vpkBaseDir, vpkBaseName);
                            if (byteData == null || byteData.Length == 0) continue;

                            using var res = new Resource();
                            using var ms = new MemoryStream(byteData);
                            res.Read(ms);

                            var (hasAg2, preset, heroKey) = ExtractAg2FromResource(res, entry.Directory, entry.FileName);
                            if (hasAg2 && preset != null && !string.IsNullOrEmpty(heroKey))
                            {
                                results[heroKey] = preset;
                            }
                        }
                        catch { }
                    }
                }
                else if (Directory.Exists(resolvedPath))
                {
                    // Loose files scan (e.g. extracted game directory)
                    var wipDir = Path.Combine(resolvedPath, "models", "heroes_wip");
                    var stagingDir = Path.Combine(resolvedPath, "models", "heroes_staging");

                    var targetDirs = new List<string>();
                    if (Directory.Exists(wipDir)) targetDirs.Add(wipDir);
                    if (Directory.Exists(stagingDir)) targetDirs.Add(stagingDir);

                    var altWipDir = Path.Combine(resolvedPath, "game", "citadel", "models", "heroes_wip");
                    var altStagingDir = Path.Combine(resolvedPath, "game", "citadel", "models", "heroes_staging");
                    if (Directory.Exists(altWipDir)) targetDirs.Add(altWipDir);
                    if (Directory.Exists(altStagingDir)) targetDirs.Add(altStagingDir);

                    var allVmdlcFiles = new List<string>();
                    foreach (var d in targetDirs)
                    {
                        allVmdlcFiles.AddRange(Directory.GetFiles(d, "*.vmdl_c", SearchOption.AllDirectories));
                    }

                    foreach (var filePath in allVmdlcFiles)
                    {
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
                        catch { }
                    }
                }
                else
                {
                    return (false, $"File or folder not found: {resolvedPath}", results);
                }

                if (results.Count == 0)
                {
                    return (false, "Scan completed, but no models with AnimGraph2 nodes were found in heroes_staging / heroes_wip.", results);
                }

                // Merge with existing base database so built-in heroes are preserved
                var merged = new Dictionary<string, HeroPreset>(HeroDatabase.GetDatabase(), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in results)
                {
                    merged[kv.Key] = kv.Value;
                }

                // Save to hero_paths.json
                var savedPath = HeroDatabase.SaveDatabase(merged);

                return (true, $"Successfully scanned and saved {merged.Count} hero preset(s) (including base heroes) to: {savedPath}", merged);
            }
            catch (Exception ex)
            {
                return (false, $"Error scanning VPK: {ex.Message}", results);
            }
        });
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

            string skel = string.Empty;
            string graph = string.Empty;
            string uiGraph = string.Empty;

            // 1. Extract Skeleton: m_vecNmSkeletonRefs = [ resource:"..." ]
            var skelMatch = Regex.Match(text, @"m_vecNmSkeletonRefs\s*=\s*\[\s*(?:resource:)?\s*""([^""]+\.vnmskel)""", RegexOptions.IgnoreCase);
            if (skelMatch.Success)
            {
                skel = skelMatch.Groups[1].Value.Trim();
            }

            // 2. Extract AnimGraphs: m_animGraph2Refs
            // Match each { m_sIdentifier = "..." m_hGraph = resource:"..." } block
            var blockMatches = Regex.Matches(text, @"\{\s*m_sIdentifier\s*=\s*""([^""]*)""[\s\S]*?m_hGraph\s*=\s*(?:resource:)?\s*""([^""]+\.vnmgraph)""\s*\}", RegexOptions.IgnoreCase);
            foreach (Match bm in blockMatches)
            {
                var id = bm.Groups[1].Value.Trim();
                var hGraph = bm.Groups[2].Value.Trim();

                if (string.IsNullOrEmpty(id) || id.Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(graph)) graph = hGraph;
                }
                else if (id.Equals("ui", StringComparison.OrdinalIgnoreCase))
                {
                    uiGraph = hGraph;
                }
            }

            // Also check reverse order of attributes (m_hGraph then m_sIdentifier)
            if (string.IsNullOrEmpty(graph) || string.IsNullOrEmpty(uiGraph))
            {
                var revMatches = Regex.Matches(text, @"\{\s*m_hGraph\s*=\s*(?:resource:)?\s*""([^""]+\.vnmgraph)""[\s\S]*?m_sIdentifier\s*=\s*""([^""]*)""\s*\}", RegexOptions.IgnoreCase);
                foreach (Match rm in revMatches)
                {
                    var hGraph = rm.Groups[1].Value.Trim();
                    var id = rm.Groups[2].Value.Trim();

                    if (string.IsNullOrEmpty(id) || id.Equals("default", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(graph)) graph = hGraph;
                    }
                    else if (id.Equals("ui", StringComparison.OrdinalIgnoreCase))
                    {
                        uiGraph = hGraph;
                    }
                }
            }

            // Clean leading prefixes
            if (skel.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                skel = skel["resource:".Length..].Trim().Trim('"');
            if (graph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                graph = graph["resource:".Length..].Trim().Trim('"');
            if (uiGraph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                uiGraph = uiGraph["resource:".Length..].Trim().Trim('"');

            // CRITICAL: ONLY return true if the model actually has AnimGraph2 / NmSkeleton nodes!
            if (string.IsNullOrWhiteSpace(skel) && string.IsNullOrWhiteSpace(graph))
            {
                return (false, null, string.Empty);
            }

            // Determine canonical Hero Key name
            var dirParts = dirName.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var heroName = dirParts.Length > 0 ? dirParts[^1] : fileName;

            heroName = Regex.Replace(heroName, @"_v\d+$", "", RegexOptions.IgnoreCase);

            if (fileName.Contains(heroName, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(fileName, heroName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, heroName + "_body", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, heroName + "_model", StringComparison.OrdinalIgnoreCase))
                {
                    // Primary hero key
                }
                else
                {
                    heroName = fileName;
                }
            }
            else
            {
                heroName = fileName;
            }

            var preset = new HeroPreset
            {
                Skel = skel,
                Graph = graph,
                UiGraph = uiGraph
            };

            return (true, preset, heroName.ToLowerInvariant());
        }
        catch
        {
            return (false, null, string.Empty);
        }
    }

    private static List<VpkEntry> ReadVpkDirectory(string vpkPath)
    {
        var list = new List<VpkEntry>();
        using var fs = new FileStream(vpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        uint signature = reader.ReadUInt32();
        if (signature != 0x55aa1234) return list;

        uint version = reader.ReadUInt32();
        uint treeSize = reader.ReadUInt32();

        if (version == 2)
        {
            fs.Seek(16, SeekOrigin.Current); // Skip V2 header extras
        }

        while (true)
        {
            string ext = ReadNullTerminatedString(reader);
            if (string.IsNullOrEmpty(ext)) break;

            while (true)
            {
                string dir = ReadNullTerminatedString(reader);
                if (string.IsNullOrEmpty(dir)) break;

                while (true)
                {
                    string filename = ReadNullTerminatedString(reader);
                    if (string.IsNullOrEmpty(filename)) break;

                    var entry = new VpkEntry
                    {
                        Extension = ext,
                        Directory = dir.Replace('\\', '/'),
                        FileName = filename,
                        CRC32 = reader.ReadUInt32(),
                        PreloadBytes = reader.ReadUInt16(),
                        ArchiveIndex = reader.ReadUInt16(),
                        EntryOffset = reader.ReadUInt32(),
                        EntryLength = reader.ReadUInt32()
                    };

                    ushort terminator = reader.ReadUInt16(); // 0xFFFF

                    if (entry.PreloadBytes > 0)
                    {
                        entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);
                    }

                    list.Add(entry);
                }
            }
        }

        return list;
    }

    private static byte[]? ExtractVpkEntryBytes(VpkEntry entry, string dirVpkPath, string baseDir, string baseName)
    {
        int totalLen = entry.PreloadBytes + (int)entry.EntryLength;
        if (totalLen == 0) return Array.Empty<byte>();

        var result = new byte[totalLen];
        int dstOffset = 0;

        if (entry.PreloadBytes > 0 && entry.PreloadData != null)
        {
            Buffer.BlockCopy(entry.PreloadData, 0, result, 0, entry.PreloadBytes);
            dstOffset = entry.PreloadBytes;
        }

        if (entry.EntryLength > 0)
        {
            string dataVpkPath;
            if (entry.ArchiveIndex == 0x7FFF)
            {
                dataVpkPath = dirVpkPath;
            }
            else
            {
                dataVpkPath = Path.Combine(baseDir, $"{baseName}_{entry.ArchiveIndex:D3}.vpk");
            }

            if (File.Exists(dataVpkPath))
            {
                using var fs = new FileStream(dataVpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(entry.EntryOffset, SeekOrigin.Begin);
                int bytesRead = fs.Read(result, dstOffset, (int)entry.EntryLength);
            }
        }

        return result;
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            byte b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
