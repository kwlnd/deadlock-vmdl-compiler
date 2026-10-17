using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class Ag2InjectionTests
{
    [Fact]
    public void InjectsListsOnceAndIsIdempotent()
    {
        var initial = CsWinCompiler.UpgradeVmdlContent(BareModel, Paths.Skel, Paths.Graph, Paths.Ui);
        Check(initial.Error == null, "Injection rejected a RootNode without model_archetype.");
        Check(Count(initial.Content, "_class = \"NmSkeletonList\"") == 1, "Missing or duplicate skeleton list.");
        Check(Count(initial.Content, "_class = \"AnimGraph2List\"") == 1, "Missing or duplicate graph list.");
        Check(initial.Content.Contains(Paths.Ui), "Missing ui graph path.");

        var repeat = CsWinCompiler.UpgradeVmdlContent(initial.Content, Paths.Skel, Paths.Graph, Paths.Ui);
        Check(repeat.Content == initial.Content, "Repeated injection is not idempotent.");
    }

    [Fact]
    public void UpdatesExistingReferences()
    {
        var changed = CsWinCompiler.UpgradeVmdlContent(InjectedModel,
            "new_skeleton.vnmskel", "new_default.vnmgraph", "new_ui.vnmgraph");
        Check(changed.Content.Contains("new_skeleton.vnmskel") &&
              changed.Content.Contains("new_default.vnmgraph") &&
              changed.Content.Contains("new_ui.vnmgraph"), "Existing AG2 references were not updated.");
        Check(!changed.Content.Contains(Paths.Graph), "Stale default graph reference remained.");
    }

    [Fact]
    public void InjectsUiGraphAlone()
    {
        var uiOnly = CsWinCompiler.UpgradeVmdlContent(BareModel, "", "", Paths.Ui,
            addSkel: false, addGraph: false, addUiGraph: true);
        Check(uiOnly.Content.Contains("_class = \"AnimGraph2\"") &&
              !uiOnly.Content.Contains("_class = \"DefaultAnimGraph2\""), "Independent ui graph injection failed.");
    }

    [Fact]
    public void RejectsMissingSkeletonPreset()
    {
        var invalid = CsWinCompiler.UpgradeVmdlContent(BareModel, "", Paths.Graph, Paths.Ui);
        Check(invalid.Error != null, "Missing skeleton preset was accepted.");
    }

    [Fact]
    public void WrapsStandaloneGraphNodes()
    {
        var standaloneModel = Header + "\n{ rootNode = { _class = \"RootNode\" children = [ " +
            "{ _class = \"DefaultAnimGraph2\" filename = \"old.vnmgraph\" }, " +
            "{ _class = \"AnimGraph2\" name = \"ui\" filename = \"old_ui.vnmgraph\" }, " +
            "] importer_notes = \"\"\"A note with { and ] characters\"\"\" } }";
        var wrapped = CsWinCompiler.UpgradeVmdlContent(standaloneModel, Paths.Skel, Paths.Graph, Paths.Ui);
        Check(wrapped.Error == null, "Standalone AG2 nodes could not be wrapped.");
        Check(Count(wrapped.Content, "_class = \"AnimGraph2List\"") == 1 &&
              Count(wrapped.Content, "_class = \"DefaultAnimGraph2\"") == 1 &&
              !wrapped.Content.Contains("old.vnmgraph"), "Standalone graph migration failed.");
    }

    [Fact]
    public void IgnoresCommentedFields()
    {
        var commentedModel = Header + "\n// rootNode = { children = [ ] }\n" +
            "{ rootNode = { _class = \"RootNode\" children = [ " +
            "{ _class = \"AnimGraph2List\" children = [ " +
            "{ _class = \"DefaultAnimGraph2\" // filename = \"commented.vnmgraph\"\n" +
            "filename = \"old.vnmgraph\" } ] } ] } }";
        var commented = CsWinCompiler.UpgradeVmdlContent(commentedModel, Paths.Skel, Paths.Graph, Paths.Ui);
        Check(commented.Error == null &&
              commented.Content.Contains("filename = \"" + Paths.Graph + "\"") &&
              commented.Content.Contains("// filename = \"commented.vnmgraph\""),
            "Commented ModelDoc text was treated as a live field.");
    }

    [Fact]
    public void KeepsSeparatorsOutOfCommentsAndKeepsCrlf()
    {
        var trailingComment = Header + "\n{\nrootNode =\n{\n_class = \"RootNode\"\nchildren =\n[\n" +
            "{\n_class = \"MaterialGroupList\"\n} // last node\n]\n}\n}";
        var afterComment = CsWinCompiler.UpgradeVmdlContent(trailingComment, Paths.Skel, Paths.Graph, Paths.Ui).Content;
        Check(afterComment.Contains("},\n{\n_class = \"NmSkeletonList\"") && !afterComment.Contains("// last node,"),
            "The separator for an injected node was written inside a trailing comment.");
        var windowsModel = BareModel.Replace("\n", "\r\n");
        Check(!Regex.IsMatch(CsWinCompiler.UpgradeVmdlContent(windowsModel, Paths.Skel, Paths.Graph, Paths.Ui).Content, "(?<!\r)\n"),
            "Injected AG2 nodes mixed LF line endings into a CRLF ModelDoc file.");
    }

    [Fact]
    public void SanitizerRemovesLooseReferencesOnly()
    {
        var stripped = Ag2Sanitizer.SanitizeVmdlContent(LooseReferences);
        Check(!stripped.CleanContent.Contains("loose.vnmskel") && !stripped.CleanContent.Contains("a.vnmgraph") &&
              stripped.CleanContent.Contains("_class = \"Note\"") &&
              Ag2Sanitizer.SanitizeVmdlContent(stripped.CleanContent).Changes.Count == 0,
            "The sanitizer left an AG2 reference, removed text inside a string, or is not idempotent.");
    }

    [Fact]
    public async Task NamedGraphsReplaceTheDefaultGraph()
    {
        var neutralGraph = new Dictionary<string, string> { ["Neutrals"] = "animgraphs/neutral_test.vnmgraph" };
        var namedOnly = CsWinCompiler.UpgradeVmdlContent(BareModel, "models/test.vnmskel", "", "",
            addUiGraph: false, namedGraphs: neutralGraph);
        Check(namedOnly.Error == null &&
              namedOnly.Content.Contains("name = \"Neutrals\"", StringComparison.Ordinal) &&
              !namedOnly.Content.Contains("DefaultAnimGraph2", StringComparison.Ordinal) &&
              !namedOnly.Content.Contains("name = \"ui\"", StringComparison.Ordinal),
            "A named-only neutral graph was rejected, renamed to default, or given a nonexistent UI graph.");
        var namedAgain = CsWinCompiler.UpgradeVmdlContent(namedOnly.Content, "models/test.vnmskel", "", "",
            addUiGraph: false, namedGraphs: neutralGraph);
        Check(namedAgain.Content == namedOnly.Content, "Named graph injection is not idempotent.");
        var namedUpdated = CsWinCompiler.UpgradeVmdlContent(namedOnly.Content, "models/test.vnmskel", "", "",
            addUiGraph: false, namedGraphs: new Dictionary<string, string> { ["Neutrals"] = "animgraphs/updated_neutral.vnmgraph" });
        Check(Count(namedUpdated.Content, "name = \"Neutrals\"") == 1 &&
              namedUpdated.Content.Contains("animgraphs/updated_neutral.vnmgraph", StringComparison.Ordinal),
            "Updating a named graph duplicated its binding or left a stale path.");
        var namedDisabled = CsWinCompiler.UpgradeVmdlContent(BareModel, "models/test.vnmskel", "", "",
            addGraph: false, addUiGraph: false, namedGraphs: neutralGraph);
        Check(!namedDisabled.Content.Contains("AnimGraph2List", StringComparison.Ordinal),
            "The graph checkbox does not disable named graph injection.");

        using var temp = new TempDir();
        var manualNeutralModel = temp[Path.Combine("abrams", "custom_neutral.vmdl")];
        Directory.CreateDirectory(Path.GetDirectoryName(manualNeutralModel)!);
        File.WriteAllText(manualNeutralModel, BareModel);
        var result = await CsWinCompiler.ProcessVmdlFileAsync(manualNeutralModel,
            skelPath: "models/test.vnmskel", graphPath: "", uiGraphPath: "", namedGraphs: neutralGraph,
            addUiGraph: false, createBackup: false, compileCsWin: false, revertVmdl: false);
        var content = File.ReadAllText(manualNeutralModel);
        Check(result.Success && content.Contains("name = \"Neutrals\"", StringComparison.Ordinal) &&
              !content.Contains("DefaultAnimGraph2", StringComparison.Ordinal),
            "A named-only manual preset inherited an unrelated default graph from its model folder.");
    }

    [Fact]
    public void EveryNeutralPresetPreparesForCswin()
    {
        HeroDatabase.UseBuiltInDatabase();
        var neutralModels = NeutralModels();
        var presets = HeroDatabase.GetVisiblePresets();
        Check(neutralModels.Count == 19 && presets.Keys.Where(key => key.StartsWith("neutral_", StringComparison.Ordinal))
                  .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(neutralModels.Keys),
            "The built-in list is missing an AG2 neutral model or includes a non-AG2 creep.");
        foreach (var (key, modelPath) in neutralModels)
        {
            var preset = presets[key];
            Check(preset.NamedGraphs.ContainsKey("Neutrals") && preset.UiGraph.Length == 0 &&
                  CsdkPaths.DetectHeroFromPath("C:/addons/test/" + modelPath[..^2]) == key,
                $"The {key} preset has an incorrect graph binding, UI graph, or automatic detection key.");
            var prepared = CsWinCompiler.UpgradeVmdlContent(BareModel, preset.Skel, preset.Graph, preset.UiGraph,
                addUiGraph: false, namedGraphs: preset.NamedGraphs);
            Check(prepared.Error == null && prepared.Content.Contains("name = \"Neutrals\"", StringComparison.Ordinal),
                $"Could not prepare the {key} AG2 preset for CSWin64.");
        }
    }

    [Fact]
    public async Task ModelDocFixWritesBackupsOnlyForChanges()
    {
        using var temp = new TempDir();
        var initial = InjectedModel;
        var vmdl = temp["sample.vmdl"];
        File.WriteAllText(vmdl, initial);
        var first = await ModelDocTools.SanitizeVmdlForModelDocAsync(vmdl);
        Check(first.Success && !File.ReadAllText(vmdl).Contains("NmSkeletonList"), "First ModelDoc fix failed.");
        Check((await ModelDocTools.SanitizeVmdlForModelDocAsync(vmdl)).Success && !File.Exists(vmdl + ".bak.1"),
            "A ModelDoc fix that changed nothing still created a backup.");
        File.WriteAllText(vmdl, initial + "\n// second version");
        var second = await ModelDocTools.SanitizeVmdlForModelDocAsync(vmdl);
        Check(second.Success, "Second ModelDoc fix failed.");
        Check(File.ReadAllText(vmdl + ".bak") == initial, "Original backup was overwritten.");
        Check(File.Exists(vmdl + ".bak.1"), "Second backup was not created.");
        File.WriteAllText(vmdl, initial + "\n// second version");
        await ModelDocTools.SanitizeVmdlForModelDocAsync(vmdl);
        Check(!File.Exists(vmdl + ".bak.2"), "An identical backup was duplicated.");

        var looseFile = temp["loose.vmdl"];
        File.WriteAllText(looseFile, LooseReferences);
        await ModelDocTools.SanitizeVmdlForModelDocAsync(looseFile);
        Check(!File.ReadAllText(looseFile).Contains("NmSkeletonReference"),
            "fix(modeldoc) and addon export disagree about standalone skeleton references.");
    }

    [Fact]
    public async Task CompileBacksUpOnlyWhenTheSourceChanges()
    {
        using var temp = new TempDir();
        var revertModel = temp[Path.Combine("abrams", "revert.vmdl")];
        Directory.CreateDirectory(Path.GetDirectoryName(revertModel)!);
        File.WriteAllText(revertModel, BareModel);
        var reverted = await CsWinCompiler.ProcessVmdlFileAsync(revertModel, Paths.Skel, Paths.Graph, Paths.Ui,
            compileCsWin: false, revertVmdl: true);
        Check(reverted.Success && File.ReadAllText(revertModel) == BareModel && !File.Exists(revertModel + ".bak"),
            "A compile that leaves the source unchanged still created a backup.");
        var saved = await CsWinCompiler.ProcessVmdlFileAsync(revertModel, Paths.Skel, Paths.Graph, Paths.Ui,
            compileCsWin: false, revertVmdl: false);
        Check(saved.Success && File.ReadAllText(revertModel + ".bak") == BareModel,
            "Saving injected AG2 nodes did not back up the original source.");
    }

    [Fact]
    public void ResolvesTheGameAddonNextToItsContentAddon()
    {
        var root = Path.Combine(Path.GetTempPath(), "x");
        var nestedContent = Path.Combine(root, "content", "work", "csdk", "content", "citadel_addons", "demo", "models", "a.vmdl");
        Check(CsdkPaths.ResolveGameAddonDir(nestedContent, null, "demo") ==
              Path.Combine(root, "content", "work", "csdk", "game", "citadel_addons", "demo"),
            "An unrelated content folder earlier in the path redirected the compiled addon.");
    }

    [Fact]
    public void BuildsResourceCompilerArguments()
    {
        Check(CsWinCompiler.BuildResourceCompilerArguments("model.vmdl", "game", false) ==
              "-f -i \"model.vmdl\" -game \"game\"" &&
              CsWinCompiler.BuildResourceCompilerArguments("model.vmdl", "game", true) ==
              "-f -skiptype vmat,vtex -i \"model.vmdl\" -game \"game\"",
            "Material compilation toggle did not produce the expected resourcecompiler arguments.");
    }
}
