using ValvePak;
using ValveResourceFormat.IO;

namespace DeadlockVmdlCompiler.Services;

/// <summary>
/// A VPK read by its internal paths, case-insensitively. Open one for the length of an operation
/// and dispose it: an open archive keeps its files locked, which would block game updates and
/// repacking the same VPK.
/// </summary>
public sealed class VpkArchive : IDisposable
{
    // GameFileLoader locks the package for its own reads, so this class locks the same object.
    private readonly Package _package = new();
    private GameFileLoader? _loader;

    public string FilePath { get; }

    /// <summary>The underlying package, for resource loaders that resolve references inside it.</summary>
    public Package Package => _package;

    private VpkArchive(string filePath)
    {
        FilePath = filePath;
        // VpkBuilder writes lowercase paths; callers may ask with the addon's original casing.
        _package.OptimizeEntriesForBinarySearch(StringComparison.OrdinalIgnoreCase);
        _package.Read(filePath);
    }

    /// <summary>Opens an archive; the caller disposes it when the operation ends.</summary>
    public static VpkArchive Open(string filePath) => new(Path.GetFullPath(filePath));

    /// <summary>Reads one file from an archive without keeping it open.</summary>
    public static byte[]? ReadFile(string vpkPath, string internalPath)
    {
        if (!File.Exists(vpkPath)) return null;
        using var archive = Open(vpkPath);
        return archive.Read(internalPath);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public bool Contains(string internalPath)
    {
        lock (_package) return _package.FindEntry(Normalize(internalPath)) != null;
    }

    public byte[]? Read(string internalPath)
    {
        lock (_package)
        {
            var entry = _package.FindEntry(Normalize(internalPath));
            if (entry == null) return null;
            _package.ReadEntry(entry, out var data);
            return data;
        }
    }

    /// <summary>Full internal paths of the files with this extension whose directory matches.</summary>
    public IReadOnlyList<string> FindFiles(string extension, Func<string, bool> directoryFilter)
    {
        lock (_package)
        {
            if (_package.Entries == null || !_package.Entries.TryGetValue(extension.TrimStart('.'), out var entries))
                return [];
            return entries
                .Where(entry => directoryFilter(entry.DirectoryName.Replace('\\', '/')))
                .Select(entry => entry.GetFullPath().Replace('\\', '/'))
                .ToList();
        }
    }

    /// <summary>
    /// A resource loader that searches this archive first, then the game's other search paths.
    /// Created on first use and disposed with the archive.
    /// </summary>
    public GameFileLoader Loader
    {
        get
        {
            lock (_package) return _loader ??= new GameFileLoader(_package, FilePath);
        }
    }

    public void Dispose()
    {
        _loader?.Dispose();
        _package.Dispose();
    }
}
