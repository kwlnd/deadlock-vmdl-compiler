namespace DeadlockVmdlCompiler.Services;

/// <summary>Enables or disables AnimationList clips before compiling, depending on which sources exist.</summary>
public static class AnimationNodes
{
    public static string DisableAnimationNodesForCompilation(string content, bool disableAnimationList = true)
    {
        // Decompiled CSDK12 sources may already contain disabled = true. The checkbox
        // must override that source state in both directions for the CSWin64 copy.
        content = ModelDocAg2Editor.SetNodeDisabled(content, "AnimationList", disableAnimationList);
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");
        return content;
    }

    public static string PrepareAnimationNodesForCompilation(string content,
        bool disableAnimationList, bool autoDetectAnims, string csdkVmdlDir,
        string csWinVmdlDir, string csdkAddonRoot, string csWinAddonRoot, Action<string>? onLog = null)
    {
        if (disableAnimationList || !autoDetectAnims)
        {
            onLog?.Invoke($"[animations] AnimationList {(disableAnimationList ? "disabled by manual override" : "enabled; automatic detection off")}");
            return DisableAnimationNodesForCompilation(content, disableAnimationList);
        }
        var result = AutoDisableAnimationNodesForCompilation(content,
            csdkVmdlDir, csWinVmdlDir, csdkAddonRoot, csWinAddonRoot);
        onLog?.Invoke($"[animations] {result.FoundCount} clip source(s) found" +
                      (result.MissingCount > 0 ? $", {result.MissingCount} missing and disabled for this compile" : string.Empty));
        return result.Content;
    }

    public static int SynchronizeAnimationSourceFiles(string content, string csdkVmdlDir,
        string csdkAddonRoot, string csWinAddonRoot, Action<string>? onLog = null)
    {
        var copied = 0;
        foreach (var filename in ModelDocAg2Editor.GetAnimationSourcePaths(content))
        {
            var source = AnimationSourceResolver.Resolve(filename, csdkAddonRoot, csdkVmdlDir);
            if (source == null) continue;
            var destination = Path.GetFullPath(Path.Combine(csWinAddonRoot,
                Path.GetRelativePath(csdkAddonRoot, source)));
            if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(csWinAddonRoot)) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Animation path leaves the CSWin64 addon: {filename}");
            if (CsWinCompiler.NeedsCopy(source, destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                copied++;
            }
        }
        if (copied > 0) onLog?.Invoke($"[sync] copied {copied} animation source(s) from outside the model folder");
        return copied;
    }

    /// <summary>Preserves available clips and disables missing sources at their exact authored paths.</summary>
    public static (string Content, int FoundCount, int MissingCount) AutoDisableAnimationNodesForCompilation(
        string content, string csdkVmdlDir, string csWinVmdlDir, string csdkAddonRoot, string csWinAddonRoot)
    {
        content = DisableAnimationNodesForCompilation(content, disableAnimationList: false);
        return ModelDocAg2Editor.ApplyAnimationFileAvailability(content, filename =>
            AnimationSourceResolver.Resolve(filename, csdkAddonRoot, csdkVmdlDir) != null ||
            AnimationSourceResolver.Resolve(filename, csWinAddonRoot, csWinVmdlDir) != null);
    }

    private static string DisableNodeByClass(string content, string className) =>
        ModelDocAg2Editor.SetNodeDisabled(content, className, disabled: true);
}
