using System.Text.RegularExpressions;
using System.Text.Json;
using DeadlockVmdlCompiler.Services;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static void CreateSteamInstallation(string directory)
{
    Directory.CreateDirectory(Path.Combine(directory, "game", "bin", "win64"));
    Directory.CreateDirectory(Path.Combine(directory, "game", "citadel"));
    // A launcher embeds the name of its content folder; engine tools beside it do not.
    File.WriteAllBytes(Path.Combine(directory, "game", "bin", "win64", "deadlock.exe"),
        System.Text.Encoding.Unicode.GetBytes("launcher for citadel"));
    File.WriteAllText(Path.Combine(directory, "game", "bin", "win64", "aaa_tool.exe"), "tool");
    File.WriteAllText(Path.Combine(directory, "game", "citadel", "pak01_dir.vpk"), "fixture");
    File.WriteAllText(Path.Combine(directory, "game", "citadel", "steam.inf"), "ClientVersion=1\nappID=1422450\n");
    // Language folders and other games carry their own steam.inf or none at all.
    Directory.CreateDirectory(Path.Combine(directory, "game", "core"));
    File.WriteAllText(Path.Combine(directory, "game", "core", "pak01_dir.vpk"), "fixture");
    File.WriteAllText(Path.Combine(directory, "game", "core", "steam.inf"), "appID=730\n");
}

static string SteamManifest(string installDir, string appId = "1422450") =>
    $"\"AppState\" {{ \"appid\" \"{appId}\" // installdir is defined below\n \"installdir\" \"{installDir}\" }}";

static string VdfPath(string path) => path.Replace("\\", "\\\\");

static bool IsWriteBlocked(Action write)
{
    try
    {
        write();
        return false;
    }
    catch (IOException)
    {
        return true;
    }
    catch (UnauthorizedAccessException)
    {
        return true;
    }
}

static int Count(string text, string token) => Regex.Matches(text, Regex.Escape(token)).Count;

const string header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc40:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
var bareModel = header + "\n{\nrootNode =\n{\n_class = \"RootNode\"\nchildren =\n[\n]\n}\n}";
var paths = (Skel: "models/heroes_staging/hornet_v3/hornet.vnmskel",
    Graph: "animgraphs/animgraph2/hero/hero.vnmgraph+vindicta.vnmgraph",
    Ui: "animgraphs/animgraph2/hero/hero_ui.vnmgraph+vindicta.vnmgraph");

var initial = VmdlPipeline.UpgradeVmdlContent(bareModel, paths.Skel, paths.Graph, paths.Ui);
Check(!initial.Changes.Any(c => c.StartsWith("Error:")), "Injection rejected a RootNode without model_archetype.");
Check(Count(initial.UpgradedContent, "_class = \"NmSkeletonList\"") == 1, "Missing or duplicate skeleton list.");
Check(Count(initial.UpgradedContent, "_class = \"AnimGraph2List\"") == 1, "Missing or duplicate graph list.");
Check(initial.UpgradedContent.Contains(paths.Ui), "Missing ui graph path.");

var repeat = VmdlPipeline.UpgradeVmdlContent(initial.UpgradedContent, paths.Skel, paths.Graph, paths.Ui);
Check(repeat.UpgradedContent == initial.UpgradedContent, "Repeated injection is not idempotent.");

var changed = VmdlPipeline.UpgradeVmdlContent(initial.UpgradedContent,
    "new_skeleton.vnmskel", "new_default.vnmgraph", "new_ui.vnmgraph");
Check(changed.UpgradedContent.Contains("new_skeleton.vnmskel") &&
      changed.UpgradedContent.Contains("new_default.vnmgraph") &&
      changed.UpgradedContent.Contains("new_ui.vnmgraph"), "Existing AG2 references were not updated.");
Check(!changed.UpgradedContent.Contains(paths.Graph), "Stale default graph reference remained.");

var uiOnly = VmdlPipeline.UpgradeVmdlContent(bareModel, "", "", paths.Ui,
    addSkel: false, addGraph: false, addUiGraph: true);
Check(uiOnly.UpgradedContent.Contains("_class = \"AnimGraph2\"") &&
      !uiOnly.UpgradedContent.Contains("_class = \"DefaultAnimGraph2\""), "Independent ui graph injection failed.");

var invalid = VmdlPipeline.UpgradeVmdlContent(bareModel, "", paths.Graph, paths.Ui);
Check(invalid.Changes.Any(c => c.StartsWith("Error:")), "Missing skeleton preset was accepted.");

var standaloneModel = header + "\n{ rootNode = { _class = \"RootNode\" children = [ " +
    "{ _class = \"DefaultAnimGraph2\" filename = \"old.vnmgraph\" }, " +
    "{ _class = \"AnimGraph2\" name = \"ui\" filename = \"old_ui.vnmgraph\" }, " +
    "] importer_notes = \"\"\"A note with { and ] characters\"\"\" } }";
var wrapped = VmdlPipeline.UpgradeVmdlContent(standaloneModel, paths.Skel, paths.Graph, paths.Ui);
Check(!wrapped.Changes.Any(c => c.StartsWith("Error:")), "Standalone AG2 nodes could not be wrapped.");
Check(Count(wrapped.UpgradedContent, "_class = \"AnimGraph2List\"") == 1 &&
      Count(wrapped.UpgradedContent, "_class = \"DefaultAnimGraph2\"") == 1 &&
      !wrapped.UpgradedContent.Contains("old.vnmgraph"), "Standalone graph migration failed.");

var commentedModel = header + "\n// rootNode = { children = [ ] }\n" +
    "{ rootNode = { _class = \"RootNode\" children = [ " +
    "{ _class = \"AnimGraph2List\" children = [ " +
    "{ _class = \"DefaultAnimGraph2\" // filename = \"commented.vnmgraph\"\n" +
    "filename = \"old.vnmgraph\" } ] } ] } }";
var commented = VmdlPipeline.UpgradeVmdlContent(commentedModel, paths.Skel, paths.Graph, paths.Ui);
Check(!commented.Changes.Any(c => c.StartsWith("Error:")) &&
      commented.UpgradedContent.Contains("filename = \"" + paths.Graph + "\"") &&
      commented.UpgradedContent.Contains("// filename = \"commented.vnmgraph\""),
    "Commented ModelDoc text was treated as a live field.");

var trailingComment = header + "\n{\nrootNode =\n{\n_class = \"RootNode\"\nchildren =\n[\n" +
    "{\n_class = \"MaterialGroupList\"\n} // last node\n]\n}\n}";
var afterComment = VmdlPipeline.UpgradeVmdlContent(trailingComment, paths.Skel, paths.Graph, paths.Ui).UpgradedContent;
Check(afterComment.Contains("},\n{\n_class = \"NmSkeletonList\"") && !afterComment.Contains("// last node,"),
    "The separator for an injected node was written inside a trailing comment.");
var windowsModel = bareModel.Replace("\n", "\r\n");
Check(!Regex.IsMatch(VmdlPipeline.UpgradeVmdlContent(windowsModel, paths.Skel, paths.Graph, paths.Ui).UpgradedContent, "(?<!\r)\n"),
    "Injected AG2 nodes mixed LF line endings into a CRLF ModelDoc file.");

var looseReferences = header + "\n{ rootNode = { _class = \"RootNode\" children = [ " +
    "{ _class = \"NmSkeletonReference\" filename = \"loose.vnmskel\" }, " +
    "{ _class = \"Note\" text = \"keep { _class = \\\"AnimGraph2\\\" } here\" }, " +
    "{ _class = \"AnimGraph2List\" children = [ { _class = \"AnimGraph2\" name = \"ui\" filename = \"a.vnmgraph\" } ] } ] } }";
var strippedReferences = Ag2Sanitizer.SanitizeVmdlContent(looseReferences);
Check(!strippedReferences.CleanContent.Contains("loose.vnmskel") && !strippedReferences.CleanContent.Contains("a.vnmgraph") &&
      strippedReferences.CleanContent.Contains("_class = \"Note\"") &&
      Ag2Sanitizer.SanitizeVmdlContent(strippedReferences.CleanContent).Changes.Count == 0,
    "The sanitizer left an AG2 reference, removed text inside a string, or is not idempotent.");

var animationModel = header + """

{ rootNode = { _class = "RootNode" children = [
    { _class = "AnimationList" disabled = true children = [
        { _class = "Folder" children = [
            { _class = "AnimFile" name = "active_legacy" disabled = false source_filename = "models/demo/active.dmx" },
            { _class = "AnimFile" name = "muted_legacy" disabled = true source_filename = "models/demo/muted.dmx" }
        ] }
    ] },
    { _class = "AnimGraph" disabled = false children = [
        { _class = "Folder" disabled = false }
    ] }
] } }
""";
var sanitizedAnimations = Ag2Sanitizer.SanitizeVmdlContent(animationModel).CleanContent;
Check(sanitizedAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
      sanitizedAnimations.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true") &&
      sanitizedAnimations.Contains("_class = \"Folder\" disabled = false"),
    "ModelDoc sanitizer changed disabled flags on children inside an animation node.");
var withAnimations = VmdlPipeline.DisableAnimationNodesForCompilation(sanitizedAnimations, disableAnimationList: false);
Check(Regex.IsMatch(withAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
      withAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
      withAnimations.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true"),
    "Unchecking disable animations did not enable the exported AnimationList without changing clips.");
Check(VmdlPipeline.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: false) == withAnimations,
    "Enabling the AnimationList is not idempotent.");
var withoutAnimations = VmdlPipeline.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: true);
Check(Regex.IsMatch(withoutAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*true\b") &&
      withoutAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false"),
    "Checking disable animations did not disable only the AnimationList.");
Check(VmdlPipeline.DisableAnimationNodesForCompilation(withoutAnimations, disableAnimationList: false) == withAnimations,
    "AnimationList state did not round-trip when toggling the checkbox.");
var animationWithoutFlag = animationModel.Replace("_class = \"AnimationList\" disabled = true",
    "_class = \"AnimationList\"");
var compiledWithoutFlag = VmdlPipeline.DisableAnimationNodesForCompilation(animationWithoutFlag, disableAnimationList: false);
Check(Regex.IsMatch(compiledWithoutFlag, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b"),
    "An AnimationList without an explicit disabled field was not enabled.");

var realAnimationVmdl = Environment.GetEnvironmentVariable("DEADLOCK_TEST_ANIMATION_VMDL");
if (!string.IsNullOrWhiteSpace(realAnimationVmdl))
{
    var originalModel = File.ReadAllText(realAnimationVmdl);
    var enabledModel = VmdlPipeline.DisableAnimationNodesForCompilation(originalModel, disableAnimationList: false);
    var originalClipCount = Regex.Matches(originalModel, @"_class\s*=\s*""AnimFile""").Count;
    Check(originalClipCount > 0 &&
          Regex.IsMatch(enabledModel, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
          Regex.Matches(enabledModel, @"_class\s*=\s*""AnimFile""").Count == originalClipCount,
        "Real ModelDoc lost animations or kept AnimationList disabled.");
    Console.WriteLine($"Real ModelDoc animation check passed ({originalClipCount} AnimFile nodes).");
}

var previewMesh = new SimpleMesh3D();
previewMesh.Vertices.AddRange([new(0, 0.5f, -0.5f), new(0, 0.5f, 0.5f), new(0, 1.5f, 0)]);
previewMesh.Normals.AddRange([System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitX]);
previewMesh.Indices.AddRange([0, 1, 2]);
previewMesh.TriangleMaterialIds.Add(0);
previewMesh.Materials.Add(new MeshTexture { Width = 1, Height = 1, Pixels = [unchecked((int)0xFFFF0000)] });
previewMesh.RecalculateBounds();
var previewRenderer = new DeadlockVmdlCompiler.Controls.SoftwareMeshRenderer();
var previewPixels = new int[160 * 120];
var previewDepth = new float[160 * 120];
var previewData = DeadlockVmdlCompiler.Controls.PreparedMesh.From(previewMesh)!;
var previewCamera = DeadlockVmdlCompiler.Controls.OrbitCamera.Frame(previewData.Center, previewData.Radius, 160f / 120f);
previewRenderer.Render(previewData, previewCamera, 160, 120, previewPixels, previewDepth);
var centrePixel = previewPixels[60 * 160 + 80];
Check(((centrePixel >> 16) & 0xFF) > 100 && (centrePixel & 0xFF) < 40 && previewDepth[60 * 160 + 80] > 0,
    "The preview did not draw a framed, textured triangle at the centre of the view.");
previewCamera.Distance = 0.01f;
previewRenderer.Render(previewData, previewCamera, 160, 120, previewPixels, previewDepth);
previewRenderer.Render(null, previewCamera, 160, 120, previewPixels, previewDepth);
Check(previewDepth.All(value => value == 0), "An empty preview still contains geometry.");

HeroDatabase.UseBuiltInDatabase();
var root = Path.Combine(Path.GetTempPath(), "deadlock-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var customList = Path.Combine(root, "my_hero_paths.json");
    var customJson = """
    {
        // Custom names must appear in the preset menu, even without a known hero.
        "my_creature": { "skel": "models/custom/creature.vnmskel", "named_graphs": { "Neutrals": "animgraphs/custom/creature.vnmgraph" }, },
        "abrams": { "skel": "models/custom/abrams.vnmskel", "graph": "animgraphs/custom/abrams.vnmgraph" },
    }
    """;
    File.WriteAllText(customList, customJson);
    Check(HeroDatabase.LoadCustomDatabase(customList) == 2 &&
          HeroDatabase.GetVisiblePresets().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { "my_creature", "abrams" }) &&
          HeroDatabase.GetVisiblePresets()["abrams"].Skel == "models/custom/abrams.vnmskel" &&
          HeroDatabase.GetVisiblePresets()["my_creature"].NamedGraphs["Neutrals"] == "animgraphs/custom/creature.vnmgraph" &&
          HeroDatabase.ActiveCustomFilePath == customList && File.ReadAllText(customList) == customJson,
        "Custom lists hide unknown names, lose overrides/bindings, add unrelated built-ins, or modify the source file.");
    var activeCustomData = HeroDatabase.GetDatabase();
    var badList = Path.Combine(root, "invalid_presets.json");
    foreach (var invalidListJson in new[] { "[]", "{}", "{\"bad\":null}", "{\"bad\":{}}",
                 "{\"bad\":{\"skel\":3}}", "{\"same\":{\"skel\":\"x\"},\"SAME\":{\"skel\":\"y\"}}",
                 "{\"bad\":{\"named_graphs\":{\"ui\":\"x.vnmgraph\"}}}" })
    {
        File.WriteAllText(badList, invalidListJson);
        var rejected = false;
        try { HeroDatabase.LoadCustomDatabase(badList); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { rejected = true; }
        Check(rejected && ReferenceEquals(HeroDatabase.GetDatabase(), activeCustomData) &&
              HeroDatabase.ActiveCustomFilePath == customList,
            "An invalid preset import replaced the active preset list.");
    }
    File.WriteAllText(customList, customJson.Replace("models/custom/creature.vnmskel", "models/custom/updated.vnmskel", StringComparison.Ordinal));
    HeroDatabase.LoadCustomDatabase(customList);
    Check(HeroDatabase.GetVisiblePresets()["my_creature"].Skel == "models/custom/updated.vnmskel",
        "Loading an edited preset file reused stale cached values.");
    var selectedFileConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { HeroPathsFile = customList }))!;
    Check(selectedFileConfig.HeroPathsFile == customList, "The chosen preset file path is not persisted in config JSON.");
    HeroDatabase.ReloadDatabase(selectedFileConfig);
    Check(HeroDatabase.ActiveCustomFilePath == customList && HeroDatabase.GetVisiblePresets().Count == 2 &&
          HeroDatabase.GetVisiblePresets()["my_creature"].Skel == "models/custom/updated.vnmskel",
        "Restarting with a selected preset file did not restore the custom list.");
    var builtInConfig = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig
        { UseBuiltInHeroPaths = true, HeroPathsFile = customList }))!;
    HeroDatabase.ReloadDatabase(builtInConfig);
    Check(HeroDatabase.ActiveCustomFilePath == null && HeroDatabase.GetVisiblePresets()["abrams"].Skel != "models/custom/abrams.vnmskel",
        "The saved default-list preference did not restore bundled paths after restart.");
    HeroDatabase.UseBuiltInDatabase();
    Check(HeroDatabase.ActiveCustomFilePath == null && HeroDatabase.GetVisiblePresets().ContainsKey("seven"),
        "Switching back to built-in presets did not restore the default list.");
    Console.WriteLine("Custom preset import, validation, and reload checks passed.");

    var neutralGraph = new Dictionary<string, string> { ["Neutrals"] = "animgraphs/neutral_test.vnmgraph" };
    var namedOnly = VmdlPipeline.UpgradeVmdlContent(bareModel, "models/test.vnmskel", "", "",
        addUiGraph: false, namedGraphs: neutralGraph);
    Check(!namedOnly.Changes.Any(change => change.StartsWith("Error:", StringComparison.Ordinal)) &&
          namedOnly.UpgradedContent.Contains("name = \"Neutrals\"", StringComparison.Ordinal) &&
          !namedOnly.UpgradedContent.Contains("DefaultAnimGraph2", StringComparison.Ordinal) &&
          !namedOnly.UpgradedContent.Contains("name = \"ui\"", StringComparison.Ordinal),
        "A named-only neutral graph was rejected, renamed to default, or given a nonexistent UI graph.");
    var namedAgain = VmdlPipeline.UpgradeVmdlContent(namedOnly.UpgradedContent, "models/test.vnmskel", "", "",
        addUiGraph: false, namedGraphs: neutralGraph);
    Check(namedAgain.UpgradedContent == namedOnly.UpgradedContent, "Named graph injection is not idempotent.");
    var namedUpdated = VmdlPipeline.UpgradeVmdlContent(namedOnly.UpgradedContent, "models/test.vnmskel", "", "",
        addUiGraph: false, namedGraphs: new Dictionary<string, string> { ["Neutrals"] = "animgraphs/updated_neutral.vnmgraph" });
    Check(Count(namedUpdated.UpgradedContent, "name = \"Neutrals\"") == 1 &&
          namedUpdated.UpgradedContent.Contains("animgraphs/updated_neutral.vnmgraph", StringComparison.Ordinal),
        "Updating a named graph duplicated its binding or left a stale path.");
    var namedDisabled = VmdlPipeline.UpgradeVmdlContent(bareModel, "models/test.vnmskel", "", "",
        addGraph: false, addUiGraph: false, namedGraphs: neutralGraph);
    Check(!namedDisabled.UpgradedContent.Contains("AnimGraph2List", StringComparison.Ordinal),
        "The graph checkbox does not disable named graph injection.");
    var manualNeutralModel = Path.Combine(root, "abrams", "custom_neutral.vmdl");
    Directory.CreateDirectory(Path.GetDirectoryName(manualNeutralModel)!);
    File.WriteAllText(manualNeutralModel, bareModel);
    var manualNeutralResult = await VmdlPipeline.ProcessVmdlFileAsync(manualNeutralModel,
        skelPath: "models/test.vnmskel", graphPath: "", uiGraphPath: "", namedGraphs: neutralGraph,
        addUiGraph: false, createBackup: false, compileCsWin: false, revertVmdl: false);
    var manualNeutralContent = File.ReadAllText(manualNeutralModel);
    Check(manualNeutralResult.Success && manualNeutralContent.Contains("name = \"Neutrals\"", StringComparison.Ordinal) &&
          !manualNeutralContent.Contains("DefaultAnimGraph2", StringComparison.Ordinal),
        "A named-only manual preset inherited an unrelated default graph from its model folder.");
    Console.WriteLine("Named neutral graph preparation checks passed.");
    using var neutralFixtureStream = System.Reflection.Assembly.GetExecutingAssembly()
        .GetManifestResourceStream("RegressionChecks.NeutralAg2Models.json");
    Check(neutralFixtureStream != null, "The neutral model reference fixture is missing.");
    var neutralModels = JsonSerializer.Deserialize<Dictionary<string, string>>(neutralFixtureStream!)!;
    var neutralPresets = HeroDatabase.GetVisiblePresets();
    Check(neutralModels.Count == 19 && neutralPresets.Keys.Where(key => key.StartsWith("neutral_", StringComparison.Ordinal))
              .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(neutralModels.Keys),
        "The built-in list is missing an AG2 neutral model or includes a non-AG2 creep.");
    foreach (var (key, modelPath) in neutralModels)
    {
        var preset = neutralPresets[key];
        Check(preset.NamedGraphs.ContainsKey("Neutrals") && preset.UiGraph.Length == 0 &&
              VmdlPipeline.DetectHeroFromPath("C:/addons/test/" + modelPath[..^2]) == key,
            $"The {key} preset has an incorrect graph binding, UI graph, or automatic detection key.");
        var preparedNeutral = VmdlPipeline.UpgradeVmdlContent(bareModel, preset.Skel, preset.Graph, preset.UiGraph,
            addUiGraph: false, namedGraphs: preset.NamedGraphs);
        Check(!preparedNeutral.Changes.Any(change => change.StartsWith("Error:", StringComparison.Ordinal)) &&
              preparedNeutral.UpgradedContent.Contains("name = \"Neutrals\"", StringComparison.Ordinal),
            $"Could not prepare the {key} AG2 preset for CSWin64.");
    }

    var steamRoot = Path.Combine(root, "unusual client location");
    var secondaryLibrary = Path.Combine(root, "Другая библиотека");
    var legacyLibrary = Path.Combine(root, "legacy library");
    foreach (var library in new[] { steamRoot, secondaryLibrary, legacyLibrary })
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
    Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
    var renamedGame = Path.Combine(secondaryLibrary, "steamapps", "common", "custom game folder");
    var legacyGame = Path.Combine(legacyLibrary, "steamapps", "common", "game");
    CreateSteamInstallation(renamedGame);
    CreateSteamInstallation(legacyGame);
    CreateSteamInstallation(Path.Combine(steamRoot, "steamapps", "common", "Deadlock"));
    var primaryManifest = Path.Combine(steamRoot, "steamapps", "appmanifest_1422450.acf");
    var secondaryManifest = Path.Combine(secondaryLibrary, "steamapps", "appmanifest_1422450.acf");
    var legacyManifest = Path.Combine(legacyLibrary, "steamapps", "appmanifest_1422450.acf");
    File.WriteAllText(primaryManifest, SteamManifest("no longer installed"));
    File.WriteAllText(secondaryManifest, SteamManifest("custom game folder"));
    File.WriteAllText(legacyManifest, SteamManifest("game"));
    File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
        $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{VdfPath(steamRoot)}\" }} " +
        $"\"1\" {{ \"path\" \"{VdfPath(secondaryLibrary)}\" \"apps\" {{ }} }} " +
        $"\"2\" {{ \"path\" \"{VdfPath(secondaryLibrary)}\" }} " +
        "// \"path\" \"Z:\\\\ignored comment\"\n }");
    File.WriteAllText(Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
        $"\"LibraryFolders\" {{ \"TimeNextStatsReport\" \"0\" \"1\" \"{VdfPath(legacyLibrary)}\" }}");
    var steamLibraries = DeadlockLocator.GetSteamLibraryFolders(new[] { steamRoot });
    Check(steamLibraries.Count == 3 && steamLibraries.Contains(secondaryLibrary) && steamLibraries.Contains(legacyLibrary),
        "Steam libraries were not discovered from modern and legacy VDF metadata with escaped paths.");
    var detectedFixture = DeadlockLocator.DetectFromSteamLibraries(steamLibraries);
    Check(detectedFixture.GameRootPath == renamedGame,
        "Steam detection guessed a folder name or failed to use installdir from the secondary library manifest.");
    Check(detectedFixture.ModDirectoryName == "citadel" &&
          Path.GetFileName(detectedFixture.DeadlockExePath) == "deadlock.exe" &&
          detectedFixture.Pak01VpkPath == Path.Combine(renamedGame, "game", "citadel", "pak01_dir.vpk"),
        "The game folder was not identified by its steam.inf, or an engine tool was taken for the launcher.");
    var relocated = Path.Combine(root, "relocated install");
    CreateSteamInstallation(relocated);
    Directory.Move(Path.Combine(relocated, "game", "citadel"), Path.Combine(relocated, "game", "renamed_mod"));
    File.WriteAllBytes(Path.Combine(relocated, "game", "bin", "win64", "deadlock.exe"),
        System.Text.Encoding.Unicode.GetBytes("launcher for renamed_mod"));
    var relocatedInfo = DeadlockLocator.ValidateAndExtractInfo(relocated);
    Check(relocatedInfo.IsValid && relocatedInfo.ModDirectoryName == "renamed_mod" &&
          relocatedInfo.Pak01VpkPath == Path.Combine(relocated, "game", "renamed_mod", "pak01_dir.vpk"),
        "Detection depends on the content folder being called citadel.");
    File.Delete(Path.Combine(relocated, "game", "renamed_mod", "steam.inf"));
    Check(!DeadlockLocator.ValidateAndExtractInfo(relocated).IsValid,
        "A folder without the game's own steam.inf was accepted as Deadlock.");
    Check(DeadlockLocator.ValidateAndExtractInfo(Path.Combine(renamedGame, "game", "citadel", "pak01_dir.vpk")).GameRootPath == renamedGame,
        "An explicit game VPK hint was not normalized to the installation root.");
    File.WriteAllText(primaryManifest, "\"AppState\" { \"appid\"");
    File.WriteAllText(secondaryManifest, SteamManifest("custom game folder", "730"));
    Check(DeadlockLocator.DetectFromSteamLibraries(steamLibraries).GameRootPath == legacyGame,
        "Malformed or wrong-app manifests prevented detection, or an installdir named game was misnormalized.");
    var outsideGame = Path.Combine(secondaryLibrary, "outside");
    CreateSteamInstallation(outsideGame);
    File.WriteAllText(secondaryManifest, SteamManifest("../../outside"));
    File.Delete(legacyManifest);
    Check(!DeadlockLocator.DetectFromSteamLibraries(steamLibraries).IsValid,
        "Steam detection accepted a path outside common or guessed an installation without a valid app manifest.");
    File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {");
    File.WriteAllText(primaryManifest, SteamManifest("Deadlock"));
    Check(DeadlockLocator.DetectFromSteamLibraries(DeadlockLocator.GetSteamLibraryFolders(new[] { steamRoot })).IsValid,
        "A damaged library list prevented detection in the primary Steam library.");
    Console.WriteLine("Steam library and app manifest detection checks passed.");
    if (args.Contains("--steam-detection"))
    {
        var installedGame = DeadlockLocator.DetectDeadlockInstallation();
        Check(installedGame.IsValid, "The real Steam installation was not found through its library list and app manifest.");
        Console.WriteLine($"Live Steam detection passed: {installedGame.GameRootPath} | {Path.GetFileName(installedGame.DeadlockExePath)} | {installedGame.ModDirectoryName}/{Path.GetFileName(installedGame.Pak01VpkPath)}");
    }

    var animationRoot = Path.Combine(root, "animation_sources");
    var animationDirectory = Path.Combine(animationRoot, "models", "demo");
    var winRoot = Path.Combine(root, "cswin_animation_sources");
    var winDirectory = Path.Combine(winRoot, "models", "demo");
    Directory.CreateDirectory(animationDirectory);
    Directory.CreateDirectory(winDirectory);
    File.WriteAllText(Path.Combine(animationDirectory, "active.dmx"), "active source");
    File.WriteAllText(Path.Combine(animationDirectory, "muted.dmx"), "muted source");
    var availableAnims = VmdlPipeline.AutoDisableAnimationNodesForCompilation(animationModel,
        animationDirectory, winDirectory, animationRoot, winRoot);
    Check(availableAnims.FoundCount == 2 && availableAnims.MissingCount == 0 &&
          Regex.IsMatch(availableAnims.Content, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
          availableAnims.Content.Contains("name = \"muted_legacy\" disabled = true", StringComparison.Ordinal),
        "Automatic animation detection kept the list disabled or unmuted an intentional clip.");
    var missingReference = animationModel.Replace("models/demo/active.dmx", "models/not_here/active.dmx", StringComparison.Ordinal);
    var wrongNameMatch = VmdlPipeline.AutoDisableAnimationNodesForCompilation(missingReference,
        animationDirectory, winDirectory, animationRoot, winRoot);
    Check(wrongNameMatch.FoundCount == 1 && wrongNameMatch.MissingCount == 1,
        "A same-named file elsewhere incorrectly satisfied an animation source path.");
    var allMissing = VmdlPipeline.AutoDisableAnimationNodesForCompilation(animationModel,
        winDirectory, winDirectory, winRoot, winRoot);
    Check(allMissing.FoundCount == 0 && allMissing.MissingCount == 2 &&
          Regex.IsMatch(allMissing.Content, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*true\b") &&
          allMissing.Content.Contains("_class = \"Folder\" disabled = false", StringComparison.Ordinal),
        "Missing-animation handling did not disable the list or changed unrelated nested nodes.");
    var forcedOff = VmdlPipeline.PrepareAnimationNodesForCompilation(animationModel, true, true,
        animationDirectory, winDirectory, animationRoot, winRoot);
    Check(forcedOff == VmdlPipeline.DisableAnimationNodesForCompilation(animationModel, true),
        "Automatic mode overrides the manual disable animationlist checkbox.");
    var strictKeep = VmdlPipeline.PrepareAnimationNodesForCompilation(missingReference, false, false,
        animationDirectory, winDirectory, animationRoot, winRoot);
    Check(strictKeep == VmdlPipeline.DisableAnimationNodesForCompilation(missingReference, false),
        "Turning automatic mode off still disables clips.");
    var relativeAnims = animationModel.Replace("models/demo/active.dmx", "active.dmx", StringComparison.Ordinal);
    Check(VmdlPipeline.AutoDisableAnimationNodesForCompilation(relativeAnims,
        animationDirectory, winDirectory, animationRoot, winRoot).FoundCount == 2,
        "Model-relative animation paths no longer resolve.");
    var sharedDirectory = Path.Combine(animationRoot, "models", "shared");
    Directory.CreateDirectory(sharedDirectory);
    var sharedBytes = new byte[] { 1, 4, 7, 10 };
    File.WriteAllBytes(Path.Combine(sharedDirectory, "shared.dmx"), sharedBytes);
    var sharedAnims = animationModel.Replace("models/demo/active.dmx", "models/shared/shared.dmx", StringComparison.Ordinal);
    Check(VmdlPipeline.SynchronizeAnimationSourceFiles(sharedAnims, animationDirectory, animationRoot, winRoot) == 2 &&
          File.ReadAllBytes(Path.Combine(winRoot, "models", "shared", "shared.dmx")).SequenceEqual(sharedBytes),
        "An animation outside the model directory was not synchronized at its exact addon-relative path.");
    var commentAnims = animationModel.Replace("name = \"active_legacy\"", "name = \"active_legacy\" description = \"uses {events}\"", StringComparison.Ordinal) +
                       "\n// { _class = \"AnimFile\" source_filename = \"missing.dmx\" }";
    Check(VmdlPipeline.AutoDisableAnimationNodesForCompilation(commentAnims,
        animationDirectory, winDirectory, animationRoot, winRoot).FoundCount == 2,
        "Comments or braces inside strings confused automatic animation detection.");
    var oldConfig = JsonSerializer.Deserialize<AppConfig>("{\"chk_disable_anim_list\":true}")!;
    var contributorConfig = JsonSerializer.Deserialize<AppConfig>("{\"chk_auto_detect_anims\":false}")!;
    Check(oldConfig.ChkDisableAnimList && oldConfig.ChkAutoDetectAnims &&
          !contributorConfig.ChkDisableAnimList && !contributorConfig.ChkAutoDetectAnims,
        "Settings from either version lose their animation mode during migration.");
    Console.WriteLine("Combined manual and automatic animation checks passed.");

    var gameDir = Path.Combine(root, "game");
    var modelDir = Path.Combine(gameDir, "models", "heroes_staging", "demo");
    Directory.CreateDirectory(modelDir);

    var deployedModel = Path.Combine(modelDir, "protected.vmdl_c");
    var compilerOutput = Path.Combine(root, "compiler-output.vmdl_c");
    var compiledBytes = new byte[] { 0x11, 0x22, 0x33, 0x44 };
    File.WriteAllBytes(deployedModel, compiledBytes);
    File.WriteAllBytes(compilerOutput, compiledBytes);
    using (CompiledModelProtection.Acquire(deployedModel, compilerOutput))
    {
        Check(File.ReadAllBytes(deployedModel).SequenceEqual(compiledBytes),
            "Protected model could not be read while packaging.");
        var protectedVpk = Path.Combine(root, "protected_pak01_dir.vpk");
        var protectedPack = await VpkBuilder.PackAddonToVpkAsync(gameDir, protectedVpk);
        Check(protectedPack.Success, "VPK packaging could not read a protected model.");
        var protectedBytes = VpkHeroScanner.ExtractFileFromVpk(protectedVpk,
            "models/heroes_staging/demo/protected.vmdl_c");
        Check(protectedBytes != null && protectedBytes.SequenceEqual(compiledBytes),
            "Protected model changed while being packaged.");
        Check(IsWriteBlocked(() =>
        {
            using var stream = new FileStream(deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.WriteByte(0xFF);
        }), "FileStream write was allowed while the model was protected.");
        Check(IsWriteBlocked(() => File.WriteAllBytes(deployedModel, new byte[] { 0xFF })),
            "File.WriteAllBytes was allowed while the model was protected.");
        var replacement = Path.Combine(root, "replacement.vmdl_c");
        File.WriteAllBytes(replacement, new byte[] { 0xFF });
        Check(IsWriteBlocked(() => File.Move(replacement, deployedModel, overwrite: true)),
            "Atomic replacement was allowed while the model was protected.");
    }

    using (var writable = new FileStream(deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        writable.WriteByte(0x55);

    File.WriteAllBytes(compilerOutput, new byte[] { 0x99, 0x22, 0x33, 0x44 });
    var mismatchRejected = false;
    try
    {
        using var _ = CompiledModelProtection.Acquire(deployedModel, compilerOutput);
    }
    catch (InvalidDataException)
    {
        mismatchRejected = true;
    }
    Check(mismatchRejected, "Protection accepted a compiler output that differs from the deployed model.");

    var original = new byte[] { 0x56, 0x50, 0x4b, 0x21 };
    File.WriteAllBytes(Path.Combine(modelDir, "demo.vmdl_c"), original);
    var vpkPath = Path.Combine(root, "pak01_dir.vpk");
    var packed = await VpkBuilder.PackAddonToVpkAsync(gameDir, vpkPath);
    Check(packed.Success, "VPK packing failed: " + packed.Message);
    var extracted = VpkHeroScanner.ExtractFileFromVpk(vpkPath, "models/heroes_staging/demo/demo.vmdl_c");
    Check(extracted != null && extracted.SequenceEqual(original), "Embedded VPK entry did not round-trip.");

    var vmdl = Path.Combine(root, "sample.vmdl");
    File.WriteAllText(vmdl, initial.UpgradedContent);
    var first = await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl);
    Check(first.Success && !File.ReadAllText(vmdl).Contains("NmSkeletonList"), "First ModelDoc fix failed.");
    Check((await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl)).Success && !File.Exists(vmdl + ".bak.1"),
        "A ModelDoc fix that changed nothing still created a backup.");
    File.WriteAllText(vmdl, initial.UpgradedContent + "\n// second version");
    var second = await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl);
    Check(second.Success, "Second ModelDoc fix failed.");
    Check(File.ReadAllText(vmdl + ".bak") == initial.UpgradedContent, "Original backup was overwritten.");
    Check(File.Exists(vmdl + ".bak.1"), "Second backup was not created.");
    File.WriteAllText(vmdl, initial.UpgradedContent + "\n// second version");
    await VmdlPipeline.SanitizeVmdlForModelDocAsync(vmdl);
    Check(!File.Exists(vmdl + ".bak.2"), "An identical backup was duplicated.");
    var looseFile = Path.Combine(root, "loose.vmdl");
    File.WriteAllText(looseFile, looseReferences);
    await VmdlPipeline.SanitizeVmdlForModelDocAsync(looseFile);
    Check(!File.ReadAllText(looseFile).Contains("NmSkeletonReference"),
        "fix(modeldoc) and addon export disagree about standalone skeleton references.");

    var revertModel = Path.Combine(root, "abrams", "revert.vmdl");
    File.WriteAllText(revertModel, bareModel);
    var reverted = await VmdlPipeline.ProcessVmdlFileAsync(revertModel, paths.Skel, paths.Graph, paths.Ui,
        compileCsWin: false, revertVmdl: true);
    Check(reverted.Success && File.ReadAllText(revertModel) == bareModel && !File.Exists(revertModel + ".bak"),
        "A compile that leaves the source unchanged still created a backup.");
    var saved = await VmdlPipeline.ProcessVmdlFileAsync(revertModel, paths.Skel, paths.Graph, paths.Ui,
        compileCsWin: false, revertVmdl: false);
    Check(saved.Success && File.ReadAllText(revertModel + ".bak") == bareModel,
        "Saving injected AG2 nodes did not back up the original source.");

    var nestedContent = Path.Combine(root, "content", "work", "csdk", "content", "citadel_addons", "demo", "models", "a.vmdl");
    Check(VmdlPipeline.ResolveGameAddonDir(nestedContent, null, "demo") ==
          Path.Combine(root, "content", "work", "csdk", "game", "citadel_addons", "demo"),
        "An unrelated content folder earlier in the path redirected the compiled addon.");

    var neutralAddon = Path.Combine(root, "scan", "content", "citadel_addons", "creeps");
    var neutralSource = Path.Combine(neutralAddon, "models", "npc_units", "neutral_mushroom_small_01");
    Directory.CreateDirectory(neutralSource);
    File.WriteAllText(Path.Combine(neutralSource, "neutral_mushroom_small_01.vmdl"), bareModel);
    File.WriteAllText(Path.Combine(neutralSource, "unrelated_prop.vmdl"), bareModel);
    var scannedNeutral = VmdlScanner.ScanAddons(Path.GetDirectoryName(neutralAddon)!).Single().HeroModels;
    Check(scannedNeutral.Count == 1 && scannedNeutral[0].Hero == "neutral_mushroom_small_01",
        "A neutral model with a built-in preset is missing from the addon model list, or props are listed.");
    Check(DeadlockHeroCatalog.GetNeutrals().Select(model => model.HeroKey).ToHashSet().SetEquals(neutralModels.Keys) &&
          DeadlockHeroCatalog.GetNeutrals().All(model => neutralModels[model.HeroKey] == model.VpkPath) &&
          DeadlockHeroCatalog.GetExportableModels().Count == 44 + neutralModels.Count,
        "The addon export catalog does not offer every AG2 neutral at its game path.");

    var unicodeDir = Path.Combine(root, "unicode_game");
    Directory.CreateDirectory(Path.Combine(unicodeDir, "models", "модель"));
    File.WriteAllBytes(Path.Combine(unicodeDir, "models", "модель", "тест.vmdl_c"), original);
    var unicodeVpk = Path.Combine(root, "unicode_dir.vpk");
    File.WriteAllText(unicodeVpk, "previous archive");
    Check((await VpkBuilder.PackAddonToVpkAsync(unicodeDir, unicodeVpk)).Success &&
          VpkHeroScanner.ExtractFileFromVpk(unicodeVpk, "models/модель/тест.vmdl_c")?.SequenceEqual(original) == true &&
          !Directory.EnumerateFiles(root, "*.tmp").Any(),
        "A non-ASCII path did not round-trip through the VPK, or a temporary archive was left behind.");

    Check(AddonCreationService.ValidateName("my_hero_mod") == null, "Valid addon name was rejected.");
    Check(AddonCreationService.ValidateName("../bad") != null &&
          AddonCreationService.ValidateName("_hidden") != null,
        "Unsafe addon name was accepted.");

    var expectedHeroNames = new[]
    {
        "Abrams", "Apollo", "Baba", "Bebop", "Billy", "Calico", "Celeste", "Deadman Danny", "Drifter", "Dynamo",
        "The Doorman", "Graves", "Grey Talon", "Haze", "Holliday", "Infernus", "Ivy",
        "Kelvin", "Lady Geist", "Lash", "McGinnis", "Mina", "Mirage", "Mo & Krill",
        "Nurse Harrow", "Paige", "Paradox", "Pocket", "Rat King", "Rem", "Seven", "Shiv", "Silver", "Sinclair",
        "Solomon", "Venator", "Victor", "Vindicta", "Violet", "Viscous", "Vyper", "Warden", "Wraith", "Yamato"
    };
    var newHeroPresets = new[]
    {
        (HeroKey: "baba", PresetKey: "baba"),
        (HeroKey: "deadman_danny", PresetKey: "deadpack"),
        (HeroKey: "nurse_harrow", PresetKey: "nurse"),
        (HeroKey: "rat_king", PresetKey: "ratking"),
        (HeroKey: "solomon", PresetKey: "chessmaster"),
        (HeroKey: "violet", PresetKey: "artist")
    };
    var catalogHeroes = DeadlockHeroCatalog.GetHeroes();
    Check(catalogHeroes.Select(hero => hero.DisplayName).SequenceEqual(expectedHeroNames),
        "The addon hero catalog does not match the current 44-hero roster.");
    Check(catalogHeroes.Select(hero => hero.HeroKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedHeroNames.Length,
        "The addon hero catalog contains duplicate hero keys.");

    using (var baselineStream = typeof(HeroDatabase).Assembly.GetManifestResourceStream(
               "DeadlockVmdlCompiler.hero_paths.json"))
    {
        Check(baselineStream != null, "The built-in AG2 preset database is missing.");
        var baselinePresets = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(baselineStream!);
        Check(baselinePresets is { Count: > 0 }, "The built-in AG2 preset database is empty.");
        var visiblePresets = HeroDatabase.GetVisiblePresets();
        Check(visiblePresets.Count == baselinePresets!.Count &&
              visiblePresets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                  .SetEquals(baselinePresets.Keys),
            "The preset menu must use the curated built-in key list, even when a local database exists.");
        var visibleHeroKeys = visiblePresets
            .Select(pair => HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey)
            .Where(key => key != null)
            .ToList();
        Check(visibleHeroKeys.Count == catalogHeroes.Count &&
              visibleHeroKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogHeroes.Count,
            "The preset menu contains duplicate heroes or is missing a hero.");
        foreach (var hero in catalogHeroes)
            Check(baselinePresets!.Any(pair =>
                    HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey == hero.HeroKey),
                $"No AG2 skeleton preset maps to {hero.DisplayName}.");
        foreach (var (heroKey, presetKey) in newHeroPresets)
        {
            var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
            Check(HeroDatabase.GetDatabase().ContainsKey(presetKey),
                $"An older local database hides the new {hero.DisplayName} preset.");
            Check(VmdlPipeline.DetectHeroFromPath("C:/addons/test/" + hero.VpkPath[..^2]) == presetKey,
                $"Exported {hero.DisplayName} does not auto-detect its AG2 preset.");
            Check(VmdlPipeline.DetectHeroFromPath($"C:/addons/test/models/{heroKey}/custom.vmdl") == presetKey,
                $"The {hero.DisplayName} public name does not auto-detect its AG2 preset.");
            Check(HeroPresetMatcher.FindKnownHero("custom_" + presetKey, baselinePresets![presetKey])?.HeroKey == heroKey,
                $"Exact AG2 references under a custom key do not identify {hero.DisplayName}.");
        }
        Check(HeroPresetMatcher.FindKnownHero("seven", baselinePresets!["seven"])?.HeroKey == "seven",
            "Seven still maps to Victor's AG2 skeleton.");
        Check(baselinePresets["familiar_wip"].UiGraph.EndsWith("+familiar.vnmgraph", StringComparison.Ordinal),
            "Rem's UI AG2 graph still points to Frank.");
        Check(HeroPresetMatcher.FindKnownHero("seven", new HeroPreset
        {
            Skel = "models/heroes_wip/frank/frank.vnmskel"
        }) == null, "A mismatched skeleton was given Seven's portrait.");
        Check(HeroPresetMatcher.FindKnownHero("custom_apollo", baselinePresets["fencer"])?.HeroKey == "apollo",
            "An exact AG2 skeleton and graph under a custom key did not identify Apollo.");
        Check(HeroPresetMatcher.FindKnownHero("custom_viscous", baselinePresets["viscous"])?.HeroKey == "viscous",
            "A shared skeleton did not use its graph to distinguish Viscous from Kelvin.");
        foreach (var (folder, key) in new[]
                 {
                     ("familiar", "familiar_wip"), ("gigawatt_prisoner", "seven"),
                     ("hornet_v3", "vindicta"), ("inferno", "infernus"),
                     ("nano_v2", "calico"), ("synth", "pocket"), ("tengu", "ivy")
                 })
            Check(VmdlPipeline.DetectHeroFromPath($"C:/addons/test/models/{folder}/model.vmdl") == key,
                $"Old model folder {folder} does not resolve to the curated preset {key}.");
    }

    var deadlockVpk = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ??
                      Environment.GetEnvironmentVariable("DEADLOCK_TEST_VPK");
    if (!string.IsNullOrWhiteSpace(deadlockVpk))
    {
        var vpkEntries = VpkHeroScanner.ReadVpkDirectory(deadlockVpk);
        var vpkPaths = vpkEntries.Select(entry =>
            $"{entry.Directory}/{entry.FileName}.{entry.Extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var catalogHero in catalogHeroes)
        {
            Check(vpkPaths.Contains(catalogHero.VpkPath),
                $"Missing model in Deadlock VPK: {catalogHero.DisplayName}.");
            if (catalogHero.IconVpkPath != null)
                Check(vpkPaths.Contains(catalogHero.IconVpkPath),
                    $"Missing small portrait in Deadlock VPK: {catalogHero.DisplayName}.");
        }
        var portraits = HeroIconLoader.LoadSmallPortraits(deadlockVpk, catalogHeroes);
        Check(portraits.Count == catalogHeroes.Count(hero => hero.IconVpkPath != null),
            "Not all available small hero portraits could be decoded.");
        Check(portraits.Values.All(png => png.Length > 8 &&
              png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4e && png[3] == 0x47),
            "A hero portrait was not decoded to PNG.");

        var heroBytes = VpkHeroScanner.ExtractFileFromVpk(deadlockVpk,
            "models/heroes_staging/hornet_v3/hornet.vmdl_c");
        Check(heroBytes is { Length: > 0 }, "Could not extract Vindicta model from Deadlock VPK.");
        using var heroResource = new Resource();
        using var stream = new MemoryStream(heroBytes!);
        heroResource.Read(stream);
        var resourceText = heroResource.DataBlock?.ToString() ?? string.Empty;
        Check(resourceText.Contains("m_animGraph2Refs") && resourceText.Contains("m_vecNmSkeletonRefs"),
            "Vindicta compiled model did not expose expected AG2 fields.");
        var heroFile = Path.Combine(root, "hornet.vmdl_c");
        File.WriteAllBytes(heroFile, heroBytes!);
        var hornetPreset = HeroDatabase.GetVisiblePresets()["vindicta"];
        var verificationError = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, hornetPreset.Graph, hornetPreset.UiGraph);
        Check(verificationError == null, "Compiled AG2 verification rejected Vindicta: " + verificationError);
        var missingGraph = VmdlPipeline.VerifyCompiledAg2References(
            heroFile, hornetPreset.Skel, "animgraphs/does_not_exist.vnmgraph", hornetPreset.UiGraph);
        Check(missingGraph?.Contains("DefaultAnimGraph2") == true,
            "Compiled AG2 verification accepted a missing graph.");
        foreach (var (heroKey, presetKey) in newHeroPresets)
        {
            var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
            var preset = HeroDatabase.GetVisiblePresets()[presetKey];
            Check(vpkPaths.Contains(preset.Skel + "_c") && vpkPaths.Contains(preset.UiGraph + "_c"),
                $"Missing skeleton or UI graph in the VPK for {hero.DisplayName}.");
            var compiledFile = Path.Combine(root, presetKey + ".vmdl_c");
            File.WriteAllBytes(compiledFile, VpkHeroScanner.ExtractFileFromVpk(deadlockVpk, hero.VpkPath)!);
            var newHeroError = VmdlPipeline.VerifyCompiledAg2References(
                compiledFile, preset.Skel, preset.Graph, preset.UiGraph);
            Check(newHeroError == null,
                $"The {hero.DisplayName} preset differs from the game's actual AG2 references: {newHeroError}");
        }
        Console.WriteLine("Deadlock VPK AG2 smoke check passed.");
        foreach (var (key, modelPath) in neutralModels)
        {
            var preset = neutralPresets[key];
            Check(vpkPaths.Contains(modelPath) && vpkPaths.Contains(preset.Skel + "_c") &&
                  preset.NamedGraphs.Values.All(graph => vpkPaths.Contains(graph + "_c")) &&
                  (preset.Graph.Length == 0 || vpkPaths.Contains(preset.Graph + "_c")),
                $"A preset references an unavailable compiled AG2 resource for {key}.");
            var neutralFile = Path.Combine(root, key + ".vmdl_c");
            File.WriteAllBytes(neutralFile, VpkHeroScanner.ExtractFileFromVpk(deadlockVpk, modelPath)!);
            var neutralError = VmdlPipeline.VerifyCompiledAg2References(neutralFile, preset.Skel,
                preset.Graph.Length == 0 ? null : preset.Graph, null, preset.NamedGraphs);
            Check(neutralError == null, $"The {key} preset disagrees with actual compiled AG2 bindings: {neutralError}");
            var wrongIdentifier = VmdlPipeline.VerifyCompiledAg2References(neutralFile, null, null, null,
                new Dictionary<string, string> { ["WrongIdentifier"] = preset.NamedGraphs["Neutrals"] });
            Check(wrongIdentifier?.Contains("WrongIdentifier") == true,
                $"Compiled AG2 verification accepted a graph under the wrong identifier for {key}.");
        }
        Console.WriteLine($"Neutral VPK AG2 references passed: {neutralModels.Count} models.");

        if (args.Contains("--new-hero-export"))
        {
            var csdkRoot = Path.Combine(root, "new_hero_csdk12");
            var contentAddons = Path.Combine(csdkRoot, "content", "citadel_addons");
            Directory.CreateDirectory(contentAddons);
            Directory.CreateDirectory(Path.Combine(csdkRoot, "game", "citadel_addons"));
            foreach (var (heroKey, presetKey) in newHeroPresets)
            {
                var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
                var addon = await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, presetKey + "_export_test");
                Check(File.Exists(addon.MainVmdlPath) &&
                      Path.GetRelativePath(addon.ContentDirectory, addon.MainVmdlPath).Replace('\\', '/') == hero.VpkPath[..^2],
                    $"The {hero.DisplayName} export lost the main model or its VPK-relative path.");
                Check(Directory.Exists(addon.GameDirectory) &&
                      VmdlScanner.ScanAddons(contentAddons).Any(candidate => candidate.Name == addon.Name),
                    $"The {hero.DisplayName} addon is absent from the CSDK12 or application list.");
                var modelText = File.ReadAllText(addon.MainVmdlPath);
                var meshPaths = Regex.Matches(modelText,
                        @"_class\s*=\s*""RenderMeshFile""[^}]*?\bfilename\s*=\s*""([^""]+\.dmx)""")
                    .Select(match => match.Groups[1].Value).Distinct().ToArray();
                var dmxPaths = Regex.Matches(modelText, @"\b(?:source_)?filename\s*=\s*""([^""]+\.dmx)""")
                    .Select(match => match.Groups[1].Value).Distinct().ToArray();
                Check(meshPaths.Length > 0 && dmxPaths.All(path =>
                        File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                    $"The {hero.DisplayName} export is missing a referenced mesh, animation or cloth DMX.");
                var materialPaths = meshPaths.SelectMany(path =>
                        Regex.Matches(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(
                                Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                            @"[A-Za-z0-9_./-]+\.vmat\b").Select(match => match.Value))
                    .Distinct().ToArray();
                Check(materialPaths.Length > 0 && materialPaths.All(path =>
                        File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                    $"The {hero.DisplayName} export is missing referenced materials.");
                Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.png", SearchOption.AllDirectories).Any(),
                    $"The {hero.DisplayName} export is missing material textures.");
                var preset = HeroDatabase.GetVisiblePresets()[presetKey];
                var animationFiles = VmdlPipeline.AutoDisableAnimationNodesForCompilation(modelText,
                    Path.GetDirectoryName(addon.MainVmdlPath)!, Path.GetDirectoryName(addon.MainVmdlPath)!,
                    addon.ContentDirectory, addon.ContentDirectory);
                Check(animationFiles.MissingCount == 0,
                    $"Automatic detection reports missing animation sources in the {hero.DisplayName} export.");
                var injected = VmdlPipeline.UpgradeVmdlContent(modelText, preset.Skel, preset.Graph,
                    preset.UiGraph);
                Check(!injected.Changes.Any(change => change.StartsWith("Error:", StringComparison.Ordinal)) &&
                      injected.UpgradedContent.Contains(preset.Skel, StringComparison.Ordinal) &&
                      injected.UpgradedContent.Contains(preset.Graph, StringComparison.Ordinal) &&
                      injected.UpgradedContent.Contains(preset.UiGraph, StringComparison.Ordinal),
                    $"The {hero.DisplayName} export cannot receive its AG2 nodes for CSWin64.");
                Console.WriteLine($"{hero.DisplayName} addon export passed: {addon.FileCount} files, {addon.ClothFileCount} cloth assets.");
            }
        }

        if (args.Contains("--addon-export"))
        {
            var csdkRoot = Path.Combine(root, "test_csdk12");
            var contentAddons = Path.Combine(csdkRoot, "content", "citadel_addons");
            var gameAddons = Path.Combine(csdkRoot, "game", "citadel_addons");
            Directory.CreateDirectory(contentAddons);
            Directory.CreateDirectory(gameAddons);
            var hero = DeadlockHeroCatalog.GetHeroes().Single(h => h.HeroKey == "wraith");
            var addon = await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                hero, "wraith_export_test", onLog: Console.WriteLine);
            Check(File.Exists(addon.MainVmdlPath), "Main hero ModelDoc was not exported.");
            Check(File.Exists(Path.Combine(addon.ContentDirectory,
                "models", "heroes_wip", "wraith", "wraith.vmdl")),
                "Main model lost its VPK-relative path.");
            Check(Directory.Exists(addon.GameDirectory), "CSDK12 game addon directory is missing.");
            var modelDmx = Directory.EnumerateFiles(addon.ContentDirectory, "*model.dmx", SearchOption.AllDirectories)
                .FirstOrDefault();
            Check(modelDmx != null, "Main model mesh DMX was not exported.");
            var dmxText = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(modelDmx!));
            var materialPaths = Regex.Matches(dmxText, @"[A-Za-z0-9_./-]+\.vmat\b")
                .Select(m => m.Value).Distinct().ToArray();
            Check(materialPaths.Length > 0 && materialPaths.All(path =>
                    File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
                "A referenced material is missing or lost its VPK-relative path.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.vmat", SearchOption.AllDirectories).Any(),
                "Material dependencies were not exported.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.png", SearchOption.AllDirectories).Any(),
                "Material texture dependencies were not exported.");
            Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.dmx", SearchOption.AllDirectories).Any(),
                "Model or animation DMX dependencies were not exported.");
            Check(addon.ClothFileCount >= 2, "Cloth proxy and grid were not exported.");
            Check(VmdlScanner.ScanAddons(contentAddons).Any(a => a.Name == "wraith_export_test"),
                "New addon is absent from the application list.");
            var duplicateRejected = false;
            try
            {
                await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, "wraith_export_test");
            }
            catch (IOException) { duplicateRejected = true; }
            Check(duplicateRejected, "Existing addon was overwritten.");
            Check(!Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
                  !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
                "Temporary addon directories remained after export.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                    hero, "cancelled_export_test", cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled &&
                  !Directory.Exists(Path.Combine(contentAddons, "cancelled_export_test")) &&
                  !Directory.Exists(Path.Combine(gameAddons, "cancelled_export_test")) &&
                  !Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
                  !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
                "Cancelled export left an addon or temporary directories.");
            Console.WriteLine($"Wraith addon export passed: {addon.FileCount} files, " +
                              $"{addon.ClothFileCount} cloth assets.");

            var neutral = DeadlockHeroCatalog.GetNeutrals().Single(model => model.HeroKey == "neutral_mushroom_small_01");
            var neutralExport = await AddonCreationService.CreateAsync(contentAddons, deadlockVpk,
                neutral, "neutral_export_test");
            var exportedNeutral = VmdlScanner.ScanAddons(contentAddons)
                .Single(candidate => candidate.Name == "neutral_export_test").HeroModels;
            Check(File.Exists(neutralExport.MainVmdlPath) &&
                  exportedNeutral.Any(model => model.FullPath == neutralExport.MainVmdlPath && model.Hero == neutral.HeroKey),
                "An exported neutral is missing its main model or is not selectable afterwards.");
            Console.WriteLine($"Neutral addon export passed: {neutralExport.FileCount} files.");
        }
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("Regression checks passed.");
