using System.Security.Cryptography;

namespace DeadlockVmdlCompiler.Services;

/// <summary>The AG2 references a compiled model must contain. Null fields are not checked.</summary>
public sealed record Ag2Expectation(
    string? Skel, string? Graph, string? UiGraph, IReadOnlyDictionary<string, string>? NamedGraphs)
{
    /// <summary>
    /// The references a compile injects for these settings: an empty path falls back to the model's
    /// default path, and named graphs replace an empty default graph.
    /// </summary>
    public static Ag2Expectation ForCompile(string targetPath, string? skel, string? graph, string? uiGraph,
        bool addSkel, bool addGraph, bool addUiGraph, IReadOnlyDictionary<string, string>? namedGraphs)
    {
        var (defaultSkel, defaultGraph, defaultUiGraph) = CsdkPaths.DeriveDefaultPaths(targetPath);
        var named = addGraph ? namedGraphs : null;
        return new Ag2Expectation(
            addSkel ? (string.IsNullOrWhiteSpace(skel) ? defaultSkel : skel) : null,
            addGraph ? (string.IsNullOrWhiteSpace(graph) ? (named is { Count: > 0 } ? null : defaultGraph) : graph) : null,
            addUiGraph ? (string.IsNullOrWhiteSpace(uiGraph) ? defaultUiGraph : uiGraph) : null,
            named);
    }

    public string? VerifyCompiled(string compiledPath) =>
        CsWinCompiler.VerifyCompiledAg2References(compiledPath, Skel, Graph, UiGraph, NamedGraphs);
}

/// <summary>
/// Compiled models held read-only between a compile and its VPK, so CSDK12 cannot replace them,
/// together with the AG2 references each was compiled with. Used from the UI thread.
/// </summary>
public sealed class ProtectedModelRegistry : IDisposable
{
    private sealed record Entry(CompiledModelProtection Guard, Ag2Expectation Expected);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    public ProtectedModelRegistry(Action<string> log) => _log = log;

    /// <summary>Raised whenever the set of protected models changes.</summary>
    public event Action? Changed;

    public int Count => _entries.Count;

    public void Protect(string sourcePath, string deployedPath, string compilerOutputPath, Ag2Expectation expected)
    {
        var guard = CompiledModelProtection.Acquire(deployedPath, compilerOutputPath);
        if (_entries.Remove(sourcePath, out var previous)) previous.Guard.Dispose();
        _entries[sourcePath] = new Entry(guard, expected);
        _log($"[protect] CSDK12 cannot overwrite {Path.GetFileName(deployedPath)} until packaging or refusal.");
        Changed?.Invoke();
    }

    public Ag2Expectation? ExpectedFor(string sourcePath) =>
        _entries.TryGetValue(sourcePath, out var entry) ? entry.Expected : null;

    /// <summary>Releases models compiled to this path, before the compiler replaces the file.</summary>
    public void ReleaseForOutput(string deployedPath)
    {
        foreach (var source in _entries
                     .Where(pair => string.Equals(pair.Value.Guard.ModelPath, deployedPath, StringComparison.OrdinalIgnoreCase))
                     .Select(pair => pair.Key).ToList())
            Release(source);
    }

    public void ReleaseForSource(string sourcePath)
    {
        if (_entries.ContainsKey(sourcePath)) Release(sourcePath);
    }

    public void ReleaseForAddon(string gameAddonDir)
    {
        foreach (var source in _entries.Where(pair => IsInAddon(pair.Value.Guard.ModelPath, gameAddonDir))
                     .Select(pair => pair.Key).ToList())
            Release(source);
    }

    public bool AnyInAddon(string gameAddonDir) =>
        _entries.Values.Any(entry => IsInAddon(entry.Guard.ModelPath, gameAddonDir));

    /// <summary>
    /// Checks that the VPK holds byte-identical copies of the selected model and of every protected
    /// model in the addon. Returns an error message, or null when all of them match.
    /// </summary>
    public string? VerifyPacked(string vpkPath, string gameAddonDir, string selectedCompiledPath)
    {
        var modelPaths = _entries.Values
            .Select(entry => entry.Guard.ModelPath)
            .Where(path => IsInAddon(path, gameAddonDir))
            .Append(selectedCompiledPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        using var archive = VpkArchive.Open(vpkPath);
        foreach (var modelPath in modelPaths)
        {
            var relativePath = Path.GetRelativePath(gameAddonDir, modelPath).Replace('\\', '/');
            var packedBytes = archive.Read(relativePath);
            if (packedBytes is null)
                return $"The VPK does not contain the compiled model: {relativePath}";

            using var deployed = File.OpenRead(modelPath);
            if (packedBytes.LongLength != deployed.Length ||
                !SHA256.HashData(packedBytes).AsSpan().SequenceEqual(SHA256.HashData(deployed)))
                return $"The VPK contains a different version of the compiled model: {relativePath}";
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
            entry.Guard.Dispose();
        _entries.Clear();
    }

    private void Release(string sourcePath)
    {
        if (!_entries.Remove(sourcePath, out var entry)) return;
        entry.Guard.Dispose();
        _log($"[protect] released: {Path.GetFileName(entry.Guard.ModelPath)}");
        Changed?.Invoke();
    }

    internal static bool IsInAddon(string modelPath, string gameAddonDir)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(gameAddonDir), modelPath);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
