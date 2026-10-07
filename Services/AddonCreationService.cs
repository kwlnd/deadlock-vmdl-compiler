using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public sealed record AddonCreationResult(
    string Name,
    string ContentDirectory,
    string GameDirectory,
    string MainVmdlPath,
    int FileCount,
    int ClothFileCount);

public static class AddonCreationService
{
    private static readonly Regex ValidName = new("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Enter an addon name.";
        if (!ValidName.IsMatch(name))
            return "Use lowercase letters, digits and underscores; start with a letter.";
        if (name.Length > 64)
            return "Keep the addon name to 64 characters or fewer.";
        return null;
    }

    public static async Task<AddonCreationResult> CreateAsync(
        string contentAddonsDirectory,
        string pak01VpkPath,
        DeadlockHeroModel hero,
        string name,
        IProgress<DecompileProgress>? progress = null,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        var nameError = ValidateName(name);
        if (nameError != null) throw new ArgumentException(nameError, nameof(name));
        if (!DeadlockHeroCatalog.GetExportableModels().Contains(hero))
            throw new ArgumentException("Choose a model from the catalog.", nameof(hero));
        if (!File.Exists(pak01VpkPath))
            throw new FileNotFoundException("Deadlock pak01_dir.vpk was not found.", pak01VpkPath);

        var contentParent = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(contentAddonsDirectory.Trim().Trim('"')));
        var contentDir = Directory.GetParent(contentParent);
        var csdkRoot = contentDir?.Parent;
        if (contentDir == null || csdkRoot == null ||
            !contentDir.Name.Equals("content", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(contentParent).Equals("citadel_addons", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(contentParent))
            throw new DirectoryNotFoundException("Select CSDK12's content/citadel_addons directory first.");

        var gameParent = Path.Combine(csdkRoot.FullName, "game", "citadel_addons");
        if (!Directory.Exists(gameParent))
            throw new DirectoryNotFoundException($"CSDK12 game/citadel_addons directory was not found: {gameParent}");

        var contentTarget = Path.Combine(contentParent, name);
        var gameTarget = Path.Combine(gameParent, name);
        if (Directory.Exists(contentTarget) || File.Exists(contentTarget) ||
            Directory.Exists(gameTarget) || File.Exists(gameTarget))
            throw new IOException($"Addon '{name}' already exists in CSDK12 content or game. Choose another name.");

        var stage = Path.Combine(contentParent, $".creating-{Guid.NewGuid():N}");
        var gameStage = Path.Combine(gameParent, $".creating-{Guid.NewGuid():N}");
        var expectedRelative = hero.VpkPath.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
            ? hero.VpkPath[..^2] : hero.VpkPath;
        var expectedModel = Path.Combine(stage,
            expectedRelative.Replace('/', Path.DirectorySeparatorChar));
        var contentMoved = false;
        var gameMoved = false;
        try
        {
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(gameStage);
            var gameinfo = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pak01VpkPath))!, "gameinfo.gi");
            var result = await DecompilerEngine.DecompileModelAsync(
                pak01VpkPath, hero.VpkPath, stage, gameinfo, progress, onLog, cancellationToken);
            if (!result.Success)
                throw new InvalidDataException(result.Message);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(expectedModel) || new FileInfo(expectedModel).Length == 0)
                throw new InvalidDataException($"Export did not produce the main model at {expectedRelative}.");

            // CSDK12 discovers the addon through paired content/game directories.
            Directory.Move(stage, contentTarget);
            contentMoved = true;
            Directory.Move(gameStage, gameTarget);
            gameMoved = true;

            return new AddonCreationResult(name, contentTarget, gameTarget,
                Path.Combine(contentTarget, expectedRelative.Replace('/', Path.DirectorySeparatorChar)),
                result.TotalFilesExtracted, result.ExtractedClothFiles.Count);
        }
        catch
        {
            // Only remove directories created by this operation. Never touch a pre-existing addon.
            TryDeleteOwnedDirectory(stage, contentParent, onLog);
            TryDeleteOwnedDirectory(gameStage, gameParent, onLog);
            if (contentMoved) TryDeleteOwnedDirectory(contentTarget, contentParent, onLog);
            if (gameMoved) TryDeleteOwnedDirectory(gameTarget, gameParent, onLog);
            throw;
        }
    }

    private static void TryDeleteOwnedDirectory(string path, string parent, Action<string>? onLog)
    {
        try
        {
            var fullParent = Path.GetFullPath(parent);
            var fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetDirectoryName(fullPath), fullParent,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Temporary addon path escaped the CSDK12 directory.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
        catch (Exception ex)
        {
            onLog?.Invoke($"[add addon cleanup error] {path}: {ex.Message}");
        }
    }
}
