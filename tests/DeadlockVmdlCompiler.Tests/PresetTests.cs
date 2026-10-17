using System.Text.Json;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class PresetTests : IDisposable
{
    public PresetTests() => HeroDatabase.UseBuiltInDatabase();

    public void Dispose() => HeroDatabase.UseBuiltInDatabase();

    internal static readonly (string HeroKey, string PresetKey)[] NewHeroPresets =
    [
        ("baba", "baba"), ("deadman_danny", "deadpack"), ("nurse_harrow", "nurse"),
        ("rat_king", "ratking"), ("solomon", "chessmaster"), ("violet", "artist")
    ];

    [Fact]
    public void CustomListsImportValidateAndReload()
    {
        using var temp = new TempDir();
        var customList = temp["my_hero_paths.json"];
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
        var badList = temp["invalid_presets.json"];
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
                $"An invalid preset import ({invalidListJson}) replaced the active preset list.");
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
    }

    [Fact]
    public void ScannerListsNeutralsWithPresetsOnly()
    {
        using var temp = new TempDir();
        var neutralAddon = temp[Path.Combine("scan", "content", "citadel_addons", "creeps")];
        var neutralSource = Path.Combine(neutralAddon, "models", "npc_units", "neutral_mushroom_small_01");
        Directory.CreateDirectory(neutralSource);
        File.WriteAllText(Path.Combine(neutralSource, "neutral_mushroom_small_01.vmdl"), BareModel);
        File.WriteAllText(Path.Combine(neutralSource, "unrelated_prop.vmdl"), BareModel);
        var scanned = VmdlScanner.ScanAddons(Path.GetDirectoryName(neutralAddon)!).Single().HeroModels;
        Check(scanned.Count == 1 && scanned[0].Hero == "neutral_mushroom_small_01",
            "A neutral model with a built-in preset is missing from the addon model list, or props are listed.");
        var neutralModels = NeutralModels();
        Check(DeadlockHeroCatalog.GetNeutrals().Select(model => model.HeroKey).ToHashSet().SetEquals(neutralModels.Keys) &&
              DeadlockHeroCatalog.GetNeutrals().All(model => neutralModels[model.HeroKey] == model.VpkPath) &&
              DeadlockHeroCatalog.GetExportableModels().Count == 44 + neutralModels.Count,
            "The addon export catalog does not offer every AG2 neutral at its game path.");
    }

    [Fact]
    public void ValidatesAddonNames()
    {
        Check(AddonCreationService.ValidateName("my_hero_mod") == null, "Valid addon name was rejected.");
        Check(AddonCreationService.ValidateName("../bad") != null && AddonCreationService.ValidateName("_hidden") != null,
            "Unsafe addon name was accepted.");
    }

    [Fact]
    public void CatalogMatchesTheRosterAndEveryHeroHasAPreset()
    {
        var expectedHeroNames = new[]
        {
            "Abrams", "Apollo", "Baba", "Bebop", "Billy", "Calico", "Celeste", "Deadman Danny", "Drifter", "Dynamo",
            "The Doorman", "Graves", "Grey Talon", "Haze", "Holliday", "Infernus", "Ivy",
            "Kelvin", "Lady Geist", "Lash", "McGinnis", "Mina", "Mirage", "Mo & Krill",
            "Nurse Harrow", "Paige", "Paradox", "Pocket", "Rat King", "Rem", "Seven", "Shiv", "Silver", "Sinclair",
            "Solomon", "Venator", "Victor", "Vindicta", "Violet", "Viscous", "Vyper", "Warden", "Wraith", "Yamato"
        };
        var catalogHeroes = DeadlockHeroCatalog.GetHeroes();
        Check(catalogHeroes.Select(hero => hero.DisplayName).SequenceEqual(expectedHeroNames),
            "The addon hero catalog does not match the current 44-hero roster.");
        Check(catalogHeroes.Select(hero => hero.HeroKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == expectedHeroNames.Length,
            "The addon hero catalog contains duplicate hero keys.");

        using var baselineStream = typeof(HeroDatabase).Assembly.GetManifestResourceStream("DeadlockVmdlCompiler.hero_paths.json");
        Assert.NotNull(baselineStream);
        var baselinePresets = JsonSerializer.Deserialize<Dictionary<string, HeroPreset>>(baselineStream)!;
        Check(baselinePresets.Count > 0, "The built-in AG2 preset database is empty.");
        var visiblePresets = HeroDatabase.GetVisiblePresets();
        Check(visiblePresets.Count == baselinePresets.Count &&
              visiblePresets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(baselinePresets.Keys),
            "The preset menu must use the curated built-in key list, even when a local database exists.");
        var visibleHeroKeys = visiblePresets
            .Select(pair => HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey)
            .Where(key => key != null)
            .ToList();
        Check(visibleHeroKeys.Count == catalogHeroes.Count &&
              visibleHeroKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == catalogHeroes.Count,
            "The preset menu contains duplicate heroes or is missing a hero.");
        foreach (var hero in catalogHeroes)
            Check(baselinePresets.Any(pair => HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value)?.HeroKey == hero.HeroKey),
                $"No AG2 skeleton preset maps to {hero.DisplayName}.");
        foreach (var (heroKey, presetKey) in NewHeroPresets)
        {
            var hero = catalogHeroes.Single(candidate => candidate.HeroKey == heroKey);
            Check(HeroDatabase.GetDatabase().ContainsKey(presetKey),
                $"An older local database hides the new {hero.DisplayName} preset.");
            Check(CsdkPaths.DetectHeroFromPath("C:/addons/test/" + hero.VpkPath[..^2]) == presetKey,
                $"Exported {hero.DisplayName} does not auto-detect its AG2 preset.");
            Check(CsdkPaths.DetectHeroFromPath($"C:/addons/test/models/{heroKey}/custom.vmdl") == presetKey,
                $"The {hero.DisplayName} public name does not auto-detect its AG2 preset.");
            Check(HeroPresetMatcher.FindKnownHero("custom_" + presetKey, baselinePresets[presetKey])?.HeroKey == heroKey,
                $"Exact AG2 references under a custom key do not identify {hero.DisplayName}.");
        }
        Check(HeroPresetMatcher.FindKnownHero("seven", baselinePresets["seven"])?.HeroKey == "seven",
            "Seven still maps to Victor's AG2 skeleton.");
        Check(baselinePresets["familiar_wip"].UiGraph.EndsWith("+familiar.vnmgraph", StringComparison.Ordinal),
            "Rem's UI AG2 graph still points to Frank.");
        Check(HeroPresetMatcher.FindKnownHero("seven", new HeroPreset { Skel = "models/heroes_wip/frank/frank.vnmskel" }) == null,
            "A mismatched skeleton was given Seven's portrait.");
        Check(HeroPresetMatcher.FindKnownHero("custom_apollo", baselinePresets["fencer"])?.HeroKey == "apollo",
            "An exact AG2 skeleton and graph under a custom key did not identify Apollo.");
        Check(HeroPresetMatcher.FindKnownHero("custom_viscous", baselinePresets["viscous"])?.HeroKey == "viscous",
            "A shared skeleton did not use its graph to distinguish Viscous from Kelvin.");
        foreach (var (folder, key) in new[]
                 {
                     ("familiar", "familiar_wip"), ("gigawatt_prisoner", "seven"), ("hornet_v3", "vindicta"),
                     ("inferno", "infernus"), ("nano_v2", "calico"), ("synth", "pocket"), ("tengu", "ivy")
                 })
            Check(CsdkPaths.DetectHeroFromPath($"C:/addons/test/models/{folder}/model.vmdl") == key,
                $"Old model folder {folder} does not resolve to the curated preset {key}.");
    }
}
