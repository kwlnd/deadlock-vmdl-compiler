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
    public static async Task<(bool Success, string Message, Dictionary<string, HeroPreset> Presets)> ScanVpkForHeroesAsync(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return (false, "Target path is empty.", new());

        var resolvedPath = Path.GetFullPath(targetPath);

        if (Directory.Exists(resolvedPath))
        {
            var candVpk = Path.Combine(resolvedPath, "pak01_dir.vpk");
            if (File.Exists(candVpk)) resolvedPath = candVpk;
            else
            {
                var candCitadelVpk = Path.Combine(resolvedPath, "game", "citadel", "pak01_dir.vpk");
                if (File.Exists(candCitadelVpk)) resolvedPath = candCitadelVpk;
                else
                {
                    var candCitadel2 = Path.Combine(resolvedPath, "citadel", "pak01_dir.vpk");
                    if (File.Exists(candCitadel2)) resolvedPath = candCitadel2;
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

                    // Scan strictly models/heroes, models/heroes_staging, models/heroes_wip
                    var targetEntries = entries.Where(e =>
                        e.Extension.Equals("vmdl_c", StringComparison.OrdinalIgnoreCase) &&
                        (e.Directory.StartsWith("models/heroes", StringComparison.OrdinalIgnoreCase) ||
                         e.Directory.StartsWith("models/characters", StringComparison.OrdinalIgnoreCase))
                    ).ToList();

                    if (targetEntries.Count == 0)
                    {
                        return (false, $"No hero models found in: {Path.GetFileName(resolvedPath)}", results);
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
                    var allVmdlcFiles = Directory.GetFiles(resolvedPath, "*.vmdl_c", SearchOption.AllDirectories)
                        .Where(f => f.Replace('\\', '/').Contains("/heroes", StringComparison.OrdinalIgnoreCase))
                        .ToList();

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
                    return (false, "Scan completed, but no models with AnimGraph2 nodes were found in the selected VPK.", results);
                }

                // Merge with base database
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

            // Must contain m_animGraph2Refs AND m_vecNmSkeletonRefs
            if (!text.Contains("m_animGraph2Refs", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("m_vecNmSkeletonRefs", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null, string.Empty);
            }

            string skel = string.Empty;
            string graph = string.Empty;
            string uiGraph = string.Empty;

            // 1. Skeleton
            var skelMatch = Regex.Match(text, @"m_vecNmSkeletonRefs\s*=\s*\[[\s\S]*?(?:resource:)?\s*""([^""]+\.vnmskel)""", RegexOptions.IgnoreCase);
            if (skelMatch.Success)
            {
                skel = skelMatch.Groups[1].Value.Trim();
            }

            // 2. AnimGraph2 references
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
                    }
                }
            }

            if (skel.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                skel = skel["resource:".Length..].Trim().Trim('"');
            if (graph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                graph = graph["resource:".Length..].Trim().Trim('"');
            if (uiGraph.StartsWith("resource:", StringComparison.OrdinalIgnoreCase))
                uiGraph = uiGraph["resource:".Length..].Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(skel) || string.IsNullOrWhiteSpace(graph))
                return (false, null, string.Empty);

            // Strict Hero Key name from .vmdl_c filename
            var heroKey = fileName.ToLowerInvariant();
            if (heroKey.EndsWith(".vmdl")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_body")) heroKey = heroKey[..^5];
            if (heroKey.EndsWith("_model")) heroKey = heroKey[..^6];
            if (heroKey.EndsWith("_base")) heroKey = heroKey[..^5];

            var preset = new HeroPreset
            {
                Skel = skel,
                Graph = graph,
                UiGraph = uiGraph
            };

            return (true, preset, heroKey);
        }
        catch
        {
            return (false, null, string.Empty);
        }
    }

    public static List<VpkEntry> ReadVpkDirectory(string vpkPath)
    {
        var entries = new List<VpkEntry>();
        using var fs = new FileStream(vpkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(fs);

        uint signature = reader.ReadUInt32();
        if (signature != 0x55aa1234)
            throw new InvalidDataException($"Invalid VPK signature: 0x{signature:X8}");

        uint version = reader.ReadUInt32();
        uint treeSize = reader.ReadUInt32();

        if (version == 2)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
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
                        Directory = dir == " " ? string.Empty : dir,
                        FileName = filename,
                        CRC32 = reader.ReadUInt32(),
                        PreloadBytes = reader.ReadUInt16(),
                        ArchiveIndex = reader.ReadUInt16(),
                        EntryOffset = reader.ReadUInt32(),
                        EntryLength = reader.ReadUInt32()
                    };

                    ushort terminator = reader.ReadUInt16();

                    if (entry.PreloadBytes > 0)
                    {
                        entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);
                    }

                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static byte[]? ExtractVpkEntryBytes(VpkEntry entry, string dirVpkPath, string vpkBaseDir, string vpkBaseName)
    {
        if (entry.EntryLength == 0 && entry.PreloadBytes > 0)
            return entry.PreloadData;

        int totalLen = (int)(entry.PreloadBytes + entry.EntryLength);
        var buffer = new byte[totalLen];

        if (entry.PreloadBytes > 0 && entry.PreloadData != null)
        {
            Buffer.BlockCopy(entry.PreloadData, 0, buffer, 0, entry.PreloadBytes);
        }

        if (entry.EntryLength > 0)
        {
            string archivePath;
            if (entry.ArchiveIndex == 0x7fff)
            {
                archivePath = dirVpkPath;
            }
            else
            {
                archivePath = Path.Combine(vpkBaseDir, $"{vpkBaseName}_{entry.ArchiveIndex:D3}.vpk");
            }

            if (!File.Exists(archivePath)) return null;

            using var afs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            afs.Seek(entry.EntryOffset, SeekOrigin.Begin);
            afs.ReadExactly(buffer, entry.PreloadBytes, (int)entry.EntryLength);
        }

        return buffer;
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length) break;
            byte b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }
}
