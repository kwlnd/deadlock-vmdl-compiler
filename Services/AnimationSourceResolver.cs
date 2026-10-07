namespace DeadlockVmdlCompiler.Services;

/// <summary>Resolves the authored animation path without substituting a same-named file.</summary>
internal static class AnimationSourceResolver
{
    internal static string? Resolve(string filename, string addonRoot, string modelDirectory)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var relative = filename.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        var fromAddonRoot = relative.StartsWith("models/", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<(string Base, string Boundary)>();
        if (!string.IsNullOrWhiteSpace(addonRoot))
            candidates.Add((fromAddonRoot ? addonRoot : modelDirectory, addonRoot));
        else if (!string.IsNullOrWhiteSpace(modelDirectory))
            candidates.Add((modelDirectory, modelDirectory));
        if (!fromAddonRoot && !string.IsNullOrWhiteSpace(addonRoot))
            candidates.Add((addonRoot, addonRoot));

        foreach (var (baseDirectory, boundary) in candidates)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) continue;
            try
            {
                var fullBoundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boundary));
                var path = Path.GetFullPath(Path.Combine(baseDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (path.StartsWith(fullBoundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                    return path;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
        }
        return null;
    }
}
