using System;
using System.Collections.Generic;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Strips AG2 nodes that crash CSDK12 ModelDoc; shared by addon export and fix(modeldoc).</summary>
public static class Ag2Sanitizer
{
    public static (string CleanContent, List<string> Changes) SanitizeVmdlContent(string content, bool disableAnimationList = true)
    {
        var changes = new List<string>();

        // Lists first, then any references left standing outside them.
        foreach (var className in new[]
                 {
                     "NmSkeletonList", "AnimGraph2List", "DefaultAnimGraph2", "AnimGraph2", "NmSkeletonReference"
                 })
        {
            var stripped = ModelDocAg2Editor.RemoveNodes(content, className);
            if (stripped == content) continue;
            content = stripped;
            changes.Add($"stripped {className}");
        }

        // Avoid missing anim clip warnings when the model is opened in ModelDoc.
        if (disableAnimationList)
        {
            var disabled = ModelDocAg2Editor.SetNodeDisabled(content, "AnimationList", disabled: true);
            if (disabled != content)
            {
                content = disabled;
                changes.Add("set disabled = true on AnimationList");
            }
        }

        var withoutGraphs = ModelDocAg2Editor.SetNodeDisabled(
            ModelDocAg2Editor.SetNodeDisabled(content, "EmptyAnimGraph", disabled: true), "AnimGraph", disabled: true);
        if (withoutGraphs != content)
        {
            content = withoutGraphs;
            changes.Add("disabled legacy anim graph nodes");
        }

        return (content, changes);
    }
}
