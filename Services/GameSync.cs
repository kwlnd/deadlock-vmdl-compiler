using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Brings an authored .vmdl up to date with the installed game (camera frame, attachments, game data).</summary>
public static class GameSync
{
    public sealed record GameSyncOptions(bool FixAttachmentFrame, bool AddAttachments, bool GameData)
    {
        public bool NeedsGameModel => AddAttachments || GameData;
    }

    public sealed record BatchSyncResult(int Synced, int Unchanged, IReadOnlyList<string> Failed)
    {
        public string Summary =>
            $"synced {Synced} model(s), {Unchanged} already current" +
            (Failed.Count > 0 ? $", {Failed.Count} failed: {string.Join(", ", Failed)}" : "") +
            ". details are in the log.";
    }

    /// <summary>
    /// Syncs every listed addon model. Each model is matched to its own game model; one model
    /// that fails is recorded and the rest still run.
    /// </summary>
    public static async Task<BatchSyncResult> SyncAllAsync(
        IEnumerable<(string Addon, DiscoveredModel Model)> models, GameSyncOptions options, string? pak01VpkPath,
        Action<string>? onLog = null)
    {
        var synced = 0;
        var unchanged = 0;
        var failed = new List<string>();
        foreach (var (addon, model) in models)
        {
            var name = $"{addon}/{Path.GetFileName(model.FullPath)}";
            try
            {
                var (success, message, changed) = await SyncFromGameAsync(
                    model.FullPath, options, pak01VpkPath, FallbackGamePath(model.Hero));
                onLog?.Invoke($"[sync from game] {name}: {message}");
                if (!success) failed.Add(name);
                else if (changed) synced++;
                else unchanged++;
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[sync from game error] {name}: {ex.Message}");
                failed.Add(name);
            }
        }
        return new BatchSyncResult(synced, unchanged, failed);
    }

    /// <summary>
    /// The game model for a discovered preset key. Keys can differ from hero keys
    /// (e.g. "fencer" is Apollo), so they go through the preset matcher.
    /// </summary>
    public static string? FallbackGamePath(string? presetKey)
    {
        if (string.IsNullOrEmpty(presetKey)) return null;
        HeroDatabase.GetDatabase().TryGetValue(presetKey, out var preset);
        return HeroPresetMatcher.FindKnownHero(presetKey, preset)?.VpkPath;
    }

    public static async Task<bool> UsesLegacyCameraFrameAsync(string vmdlPath)
    {
        if (!File.Exists(vmdlPath)) return false;
        var content = await File.ReadAllTextAsync(vmdlPath);
        return ModelDocAg2Editor.UsesLegacyRootFrame(content);
    }

    public static ModelDocEdit SyncFromGameContent(
        string content, string gameContent, GameSyncOptions options) =>
        ModelDocAg2Editor.SyncFromGame(content, gameContent, options.AddAttachments, options.GameData);

    /// <summary>
    /// Brings an authored .vmdl up to date with the installed game: converts old-frame root
    /// attachments and copies the selected parts of the current game model into it.
    /// </summary>
    public static async Task<OperationResult> SyncFromGameAsync(
        string vmdlPath,
        GameSyncOptions options,
        string? pak01VpkPath,
        string? fallbackVpkPath,
        bool createBackup = true,
        Action<string>? onLog = null)
    {
        vmdlPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(vmdlPath))
            return OperationResult.Fail($"File not found: {vmdlPath}");

        var original = await File.ReadAllTextAsync(vmdlPath);
        var content = original;
        var changes = new List<string>();
        if (options.FixAttachmentFrame)
        {
            var migration = ModelDocAg2Editor.MigrateRootAttachments(content);
            if (migration.Error != null)
                return OperationResult.Fail(migration.Error);
            content = migration.Content;
            changes.AddRange(migration.Changes);
        }

        if (options.NeedsGameModel)
        {
            if (string.IsNullOrEmpty(pak01VpkPath) || !File.Exists(pak01VpkPath))
                return OperationResult.Fail("Deadlock's pak01_dir.vpk was not found; select it to copy data from the game.");
            var gameText = await Task.Run(() => GameModelSource.LoadVmdl(pak01VpkPath,
                [GameModelSource.GetVpkPath(vmdlPath), fallbackVpkPath], out var usedPath)
                is { } text ? (text, usedPath) : default);
            if (gameText.text is null)
                return OperationResult.Fail("The game has no model at this addon path, and the hero preset does not name one.");
            onLog?.Invoke($"[sync from game] read {gameText.usedPath}");

            var sync = SyncFromGameContent(content, gameText.text, options);
            if (sync.Error != null)
                return OperationResult.Fail(sync.Error);
            content = sync.Content;
            changes.AddRange(sync.Changes);
        }

        if (content == original)
            return OperationResult.Ok("The model already matches the game; nothing was changed", changed: false);

        if (createBackup)
            ModelDocTools.CreateUniqueBackup(vmdlPath);
        await File.WriteAllTextAsync(vmdlPath, content);
        return OperationResult.Ok("Synced from game: " + string.Join("; ", changes), changed: true);
    }
}
