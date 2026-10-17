using System.Text;
using ValveResourceFormat;
using ValveResourceFormat.IO;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Reads the current game's version of a model as ModelDoc text.</summary>
public static class GameModelSource
{
    /// <summary>
    /// Maps an addon model (".../models/heroes_wip/abrams/abrams.vmdl") to its VPK path.
    /// Addons keep the original game paths, so the part from "models/" on is the VPK path.
    /// </summary>
    public static string? GetVpkPath(string vmdlPath)
    {
        var normalized = vmdlPath.Replace('\\', '/');
        var index = normalized.LastIndexOf("/models/", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : normalized[(index + 1)..] + "_c";
    }

    /// <summary>Decompiles the game model to ModelDoc text, or returns null when the VPK lacks it.</summary>
    public static string? LoadVmdl(string pak01VpkPath, IEnumerable<string?> vpkPaths, out string? usedPath)
    {
        usedPath = null;
        var archive = VpkArchive.Game(pak01VpkPath);
        // The loader searches pak01 before any mounted addon, so installed mods cannot shadow the game model.
        var loader = archive.Loader;
        foreach (var path in vpkPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!.Replace('\\', '/')))
        {
            var compiled = path.EndsWith("_c", StringComparison.OrdinalIgnoreCase) ? path : path + "_c";
            if (!archive.Contains(compiled)) continue;
            using var resource = loader.LoadFile(compiled);
            if (resource == null) continue;
            using var content = new ModelExtract(resource, loader).ToContentFile();
            if (content.Data is not { Length: > 0 } data) continue;
            usedPath = compiled;
            return Encoding.UTF8.GetString(data);
        }
        return null;
    }
}
