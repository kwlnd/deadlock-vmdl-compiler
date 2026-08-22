using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DeadlockVmdlCompiler.Services;

public class VpkPackResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public string OutputVpkPath { get; set; } = string.Empty;
}

public static class VpkBuilder
{
    /// <summary>
    /// Builds a standard Source 2 .vpk file from the compiled game addon directory.
    /// Excludes:
    /// - Any paths containing "_bakeresourcecache"
    /// - Any paths starting with or containing "materials/default/"
    /// - Any ".bin" cache files
    /// - ServerConfig.vdf, *.bak, *.vpk
    /// </summary>
    public static async Task<VpkPackResult> PackAddonToVpkAsync(string sourceGameDir, string outputVpkPath)
    {
        var result = new VpkPackResult();

        if (string.IsNullOrWhiteSpace(sourceGameDir) || !Directory.Exists(sourceGameDir))
        {
            result.Success = false;
            result.Message = $"Source directory does not exist: {sourceGameDir}";
            return result;
        }

        if (string.IsNullOrWhiteSpace(outputVpkPath))
        {
            result.Success = false;
            result.Message = "Output VPK path is empty.";
            return result;
        }

        return await Task.Run(() =>
        {
            try
            {
                var normSource = Path.GetFullPath(sourceGameDir);
                var allFiles = Directory.GetFiles(normSource, "*.*", SearchOption.AllDirectories);

                var validFiles = new List<(string RelPath, string Ext, string Dir, string FileNameWithoutExt, string FullPath, long Length)>();

                foreach (var file in allFiles)
                {
                    var relPath = Path.GetRelativePath(normSource, file).Replace('\\', '/');
                    var lowerRel = relPath.ToLowerInvariant();

                    // Blacklist Filter 1: _bakeresourcecache
                    if (lowerRel.Contains("_bakeresourcecache"))
                        continue;

                    // Blacklist Filter 2: materials/default/
                    if (lowerRel.StartsWith("materials/default/") || lowerRel.Contains("/materials/default/"))
                        continue;

                    // Blacklist Filter 3: all .bin files
                    if (lowerRel.EndsWith(".bin"))
                        continue;

                    // Blacklist Filter 4: ServerConfig.vdf, *.bak, *.vpk
                    if (lowerRel.EndsWith("serverconfig.vdf") || lowerRel.EndsWith(".bak") || lowerRel.EndsWith(".vpk"))
                        continue;

                    var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
                    if (string.IsNullOrEmpty(ext)) ext = " ";

                    var dir = Path.GetDirectoryName(relPath)?.Replace('\\', '/').ToLowerInvariant() ?? " ";
                    if (string.IsNullOrEmpty(dir)) dir = " ";

                    var fn = Path.GetFileNameWithoutExtension(file);

                    var fi = new FileInfo(file);
                    validFiles.Add((relPath, ext, dir, fn, file, fi.Length));
                }

                if (validFiles.Count == 0)
                {
                    result.Success = false;
                    result.Message = "No valid compiled files found to package (all files were filtered out or directory is empty).";
                    return result;
                }

                // Group by extension -> directory -> files
                var tree = new SortedDictionary<string, SortedDictionary<string, List<(string Fn, string FullPath, long Length)>>>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in validFiles)
                {
                    if (!tree.TryGetValue(item.Ext, out var dirDict))
                    {
                        dirDict = new SortedDictionary<string, List<(string Fn, string FullPath, long Length)>>(StringComparer.OrdinalIgnoreCase);
                        tree[item.Ext] = dirDict;
                    }

                    if (!dirDict.TryGetValue(item.Dir, out var fileList))
                    {
                        fileList = new List<(string Fn, string FullPath, long Length)>();
                        dirDict[item.Dir] = fileList;
                    }

                    fileList.Add((item.FileNameWithoutExt, item.FullPath, item.Length));
                }

                // Calculate Tree Size & Build Tree Entries
                using var treeStream = new MemoryStream();
                using var treeWriter = new BinaryWriter(treeStream);

                var fileOffsetMap = new List<(string FullPath, uint EntryOffset, uint EntryLength)>();
                uint currentDataOffset = 0;

                foreach (var extKv in tree)
                {
                    WriteNullTerminated(treeWriter, extKv.Key);

                    foreach (var dirKv in extKv.Value)
                    {
                        WriteNullTerminated(treeWriter, dirKv.Key);

                        foreach (var file in dirKv.Value)
                        {
                            WriteNullTerminated(treeWriter, file.Fn);

                            // Calculate CRC32 of file
                            var fileBytes = File.ReadAllBytes(file.FullPath);
                            uint crc = ComputeCrc32(fileBytes);

                            treeWriter.Write(crc);                      // CRC32 (4 bytes)
                            treeWriter.Write((ushort)0);                 // PreloadBytes (2 bytes)
                            treeWriter.Write((ushort)0x7FFF);             // ArchiveIndex (2 bytes - embedded)
                            treeWriter.Write(currentDataOffset);         // EntryOffset (4 bytes)
                            treeWriter.Write((uint)fileBytes.Length);     // EntryLength (4 bytes)
                            treeWriter.Write((ushort)0xFFFF);            // Terminator (2 bytes)

                            fileOffsetMap.Add((file.FullPath, currentDataOffset, (uint)fileBytes.Length));
                            currentDataOffset += (uint)fileBytes.Length;
                        }

                        treeWriter.Write((byte)0); // End of Directory
                    }

                    treeWriter.Write((byte)0); // End of Extension
                }

                treeWriter.Write((byte)0); // End of Tree
                treeWriter.Flush();

                var treeBytes = treeStream.ToArray();
                uint treeSize = (uint)treeBytes.Length;

                // Write final VPK File (Header + Tree + Data)
                Directory.CreateDirectory(Path.GetDirectoryName(outputVpkPath)!);
                using var outFs = new FileStream(outputVpkPath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var outWriter = new BinaryWriter(outFs);

                // Header (12 bytes for VPK v1)
                outWriter.Write((uint)0x55aa1234); // Signature
                outWriter.Write((uint)1);          // Version 1
                outWriter.Write(treeSize);         // TreeSize

                // Tree
                outWriter.Write(treeBytes);

                // Data Section
                long totalBytesWritten = 0;
                foreach (var f in fileOffsetMap)
                {
                    var bytes = File.ReadAllBytes(f.FullPath);
                    outWriter.Write(bytes);
                    totalBytesWritten += bytes.Length;
                }

                outWriter.Flush();

                result.Success = true;
                result.FileCount = validFiles.Count;
                result.TotalBytes = totalBytesWritten;
                result.OutputVpkPath = outputVpkPath;
                result.Message = $"Successfully packed {validFiles.Count} file(s) ({totalBytesWritten / 1024.0:F1} KB) into: {outputVpkPath}";
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Failed to build VPK: {ex.Message}";
                return result;
            }
        });
    }

    private static void WriteNullTerminated(BinaryWriter writer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        writer.Write(bytes);
        writer.Write((byte)0);
    }

    private static readonly uint[] Crc32Table = InitializeCrc32Table();

    private static uint[] InitializeCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint entry = i;
            for (int j = 0; j < 8; j++)
            {
                if ((entry & 1) == 1)
                    entry = (entry >> 1) ^ 0xEDB88320;
                else
                    entry >>= 1;
            }
            table[i] = entry;
        }
        return table;
    }

    public static uint ComputeCrc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte index = (byte)((crc & 0xFF) ^ bytes[i]);
            crc = (crc >> 8) ^ Crc32Table[index];
        }
        return ~crc;
    }
}
