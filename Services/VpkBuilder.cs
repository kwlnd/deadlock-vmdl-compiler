using System.IO.Hashing;
using System.Text;

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

                // CRCs are filled in after the data is written, so every file is read once.
                var fileOffsetMap = new List<(string FullPath, long CrcPosition, uint EntryLength)>();
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

                            var crcPosition = treeStream.Position;
                            treeWriter.Write(0u);                        // CRC32 (4 bytes), patched later
                            treeWriter.Write((ushort)0);                 // PreloadBytes (2 bytes)
                            treeWriter.Write((ushort)0x7FFF);             // ArchiveIndex (2 bytes - embedded)
                            treeWriter.Write(currentDataOffset);         // EntryOffset (4 bytes)
                            treeWriter.Write((uint)file.Length);         // EntryLength (4 bytes)
                            treeWriter.Write((ushort)0xFFFF);            // Terminator (2 bytes)

                            fileOffsetMap.Add((file.FullPath, crcPosition, (uint)file.Length));
                            currentDataOffset += (uint)file.Length;
                        }

                        treeWriter.Write((byte)0); // End of Directory
                    }

                    treeWriter.Write((byte)0); // End of Extension
                }

                treeWriter.Write((byte)0); // End of Tree
                treeWriter.Flush();

                var treeBytes = treeStream.ToArray();
                uint treeSize = (uint)treeBytes.Length;

                // Write beside the destination and swap at the end, so a failed
                // run never leaves a truncated archive over the previous one.
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputVpkPath))!);
                var tempVpkPath = outputVpkPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                long totalBytesWritten = 0;
                try
                {
                    using (var outFs = new FileStream(tempVpkPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    using (var outWriter = new BinaryWriter(outFs))
                    {
                        const int headerSize = 12;
                        // Header (12 bytes for VPK v1)
                        outWriter.Write((uint)0x55aa1234); // Signature
                        outWriter.Write((uint)1);          // Version 1
                        outWriter.Write(treeSize);         // TreeSize

                        // Tree
                        outWriter.Write(treeBytes);

                        // Data Section
                        outWriter.Flush();
                        var crcs = new uint[fileOffsetMap.Count];
                        var buffer = new byte[1 << 20];
                        var crc = new Crc32();
                        for (var i = 0; i < fileOffsetMap.Count; i++)
                        {
                            var f = fileOffsetMap[i];
                            // Sharing read only keeps writers out while the file is copied.
                            using var input = new FileStream(f.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                1, FileOptions.SequentialScan);
                            crc.Reset();
                            long copied = 0;
                            int read;
                            while ((read = input.Read(buffer)) > 0)
                            {
                                crc.Append(buffer.AsSpan(0, read));
                                outFs.Write(buffer, 0, read);
                                copied += read;
                            }
                            if (copied != f.EntryLength)
                                throw new IOException($"File changed while packaging: {f.FullPath}");
                            crcs[i] = crc.GetCurrentHashAsUInt32();
                            totalBytesWritten += copied;
                        }

                        for (var i = 0; i < fileOffsetMap.Count; i++)
                        {
                            outFs.Position = headerSize + fileOffsetMap[i].CrcPosition;
                            outWriter.Write(crcs[i]);
                        }
                    }
                    File.Move(tempVpkPath, outputVpkPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempVpkPath)) File.Delete(tempVpkPath);
                }

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
}
