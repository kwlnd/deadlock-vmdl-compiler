using ValvePak;
using ValveResourceFormat.IO;

namespace DeadlockVmdlCompiler.Services;

/// <summary>
/// A VPK read by its internal paths. The game archive is opened once and shared through
/// <see cref="Game"/>; archives this app writes are opened per use with <see cref="ReadFile"/>.
/// </summary>
public sealed class VpkArchive : IDisposable
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, (DateTime Stamp, VpkArchive Archive)> GameCache =
        new(StringComparer.OrdinalIgnoreCase);

    // GameFileLoader locks the package for its own reads, so this class locks the same object.
    private readonly Package _package = new();
    private GameFileLoader? _loader;

    public string FilePath { get; }

    /// <summary>The underlying package, for resource loaders that resolve references inside it.</summary>
    public Package Package => _package;

    private VpkArchive(string filePath)
    {
        FilePath = filePath;
        _package.Read(filePath);
    }

    /// <summary>Opens an archive for one use; the caller disposes it.</summary>
    public static VpkArchive Open(string filePath) => new(Path.GetFullPath(filePath));

    /// <summary>
    /// The shared instance of a game archive such as pak01_dir.vpk. It is reopened when the
    /// file changes on disk (a game update) and must not be disposed by callers.
    /// </summary>
    public static VpkArchive Game(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var stamp = File.GetLastWriteTimeUtc(full);
        lock (CacheLock)
        {
            if (GameCache.TryGetValue(full, out var cached) && cached.Stamp == stamp)
                return cached.Archive;
            // A replaced archive may still be in use by a running read; let the GC close it.
            var archive = new VpkArchive(full);
            GameCache[full] = (stamp, archive);
            return archive;
        }
    }

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
    /// Created on first use and shared, like the archive itself.
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
