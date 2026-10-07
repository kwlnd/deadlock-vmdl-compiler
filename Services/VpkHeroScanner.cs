using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

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
    public long EmbeddedDataOffset { get; set; }
}

public static class VpkHeroScanner
{
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

        long headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new InvalidDataException($"Unsupported VPK version: {version}")
        };

        if (fs.Length < headerSize || treeSize > fs.Length - headerSize)
            throw new InvalidDataException("VPK tree extends beyond the directory file.");

        if (version == 2)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
        }

        long treeEnd = checked(headerSize + treeSize);

        while (true)
        {
            string ext = ReadNullTerminatedString(reader, treeEnd);
            if (string.IsNullOrEmpty(ext)) break;

            while (true)
            {
                string dir = ReadNullTerminatedString(reader, treeEnd);
                if (string.IsNullOrEmpty(dir)) break;

                while (true)
                {
                    string filename = ReadNullTerminatedString(reader, treeEnd);
                    if (string.IsNullOrEmpty(filename)) break;

                    EnsureTreeBytesAvailable(reader, treeEnd, sizeof(uint) + sizeof(ushort) * 2 + sizeof(uint) * 2 + sizeof(ushort));

                    var entry = new VpkEntry
                    {
                        Extension = ext,
                        Directory = dir == " " ? string.Empty : dir,
                        FileName = filename,
                        CRC32 = reader.ReadUInt32(),
                        PreloadBytes = reader.ReadUInt16(),
                        ArchiveIndex = reader.ReadUInt16(),
                        EntryOffset = reader.ReadUInt32(),
                        EntryLength = reader.ReadUInt32(),
                        EmbeddedDataOffset = treeEnd
                    };

                    ushort terminator = reader.ReadUInt16();
                    if (terminator != 0xffff)
                        throw new InvalidDataException("Invalid VPK entry terminator.");

                    if (entry.PreloadBytes > 0)
                    {
                        EnsureTreeBytesAvailable(reader, treeEnd, entry.PreloadBytes);
                        entry.PreloadData = reader.ReadBytes(entry.PreloadBytes);
                    }

                    entries.Add(entry);
                }
            }
        }

        if (reader.BaseStream.Position != treeEnd)
            throw new InvalidDataException("VPK tree size does not match its parsed contents.");

        return entries;
    }

    public static byte[]? ExtractVpkEntryBytes(VpkEntry entry, string dirVpkPath, string vpkBaseDir, string vpkBaseName)
    {
        if (entry.EntryLength == 0 && entry.PreloadBytes > 0)
            return entry.PreloadData;

        ulong totalLength = (ulong)entry.PreloadBytes + entry.EntryLength;
        if (totalLength > int.MaxValue)
            throw new InvalidDataException("VPK entry is too large to extract.");

        int totalLen = (int)totalLength;
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
            long dataOffset = entry.ArchiveIndex == 0x7fff
                ? checked(entry.EmbeddedDataOffset + entry.EntryOffset)
                : entry.EntryOffset;
            if (dataOffset < 0 || dataOffset > afs.Length || entry.EntryLength > afs.Length - dataOffset)
                throw new InvalidDataException("VPK entry data extends beyond its archive file.");

            afs.Seek(dataOffset, SeekOrigin.Begin);
            afs.ReadExactly(buffer, entry.PreloadBytes, (int)entry.EntryLength);
        }

        return buffer;
    }

    public static byte[]? ExtractFileFromVpk(string vpkPath, string internalPath)
    {
        try
        {
            if (!File.Exists(vpkPath)) return null;
            var entries = ReadVpkDirectory(vpkPath);

            var cleanTarget = internalPath.Replace('\\', '/').TrimStart('/');
            var targetDir = Path.GetDirectoryName(cleanTarget)?.Replace('\\', '/') ?? string.Empty;
            var targetExt = Path.GetExtension(cleanTarget).TrimStart('.');
            var targetName = Path.GetFileNameWithoutExtension(cleanTarget);

            var match = entries.FirstOrDefault(e =>
                e.Extension.Equals(targetExt, StringComparison.OrdinalIgnoreCase) &&
                e.FileName.Equals(targetName, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(targetDir) || e.Directory.Equals(targetDir, StringComparison.OrdinalIgnoreCase)));

            if (match != null)
            {
                var vpkBaseDir = Path.GetDirectoryName(vpkPath) ?? string.Empty;
                var vpkBaseName = Path.GetFileNameWithoutExtension(vpkPath);
                if (vpkBaseName.EndsWith("_dir", StringComparison.OrdinalIgnoreCase))
                {
                    vpkBaseName = vpkBaseName[..^4];
                }

                return ExtractVpkEntryBytes(match, vpkPath, vpkBaseDir, vpkBaseName);
            }
        }
        catch { }

        return null;
    }

    private static string ReadNullTerminatedString(BinaryReader reader, long limit)
    {
        var bytes = new List<byte>();
        while (true)
        {
            if (reader.BaseStream.Position >= limit)
                throw new InvalidDataException("VPK tree contains an unterminated string.");
            byte b = reader.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
        }
        // VpkBuilder writes UTF-8 names; ASCII game archives read the same either way.
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void EnsureTreeBytesAvailable(BinaryReader reader, long treeEnd, long byteCount)
    {
        if (byteCount < 0 || reader.BaseStream.Position > treeEnd - byteCount)
            throw new InvalidDataException("VPK entry extends beyond the declared tree size.");
    }
}
