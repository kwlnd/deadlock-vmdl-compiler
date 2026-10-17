using DeadlockVmdlCompiler.Services;
using ValveResourceFormat;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

/// <summary>Checks against the installed game. Set DEADLOCK_TEST_VPK to its pak01_dir.vpk.</summary>
public class GameVpkTests : IDisposable
{
    private readonly TempDir _temp = new();

    public GameVpkTests() => HeroDatabase.UseBuiltInDatabase();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryHeroModelAndPortraitIsInTheGame()
    {
        var vpk = RequireGameVpk();
        using var archive = VpkArchive.Open(vpk);
        var heroes = DeadlockHeroCatalog.GetHeroes();
        foreach (var hero in heroes)
        {
            Check(archive.Contains(hero.VpkPath), $"Missing model in Deadlock VPK: {hero.DisplayName}.");
            if (hero.IconVpkPath != null)
                Check(archive.Contains(hero.IconVpkPath), $"Missing small portrait in Deadlock VPK: {hero.DisplayName}.");
        }
        var portraits = HeroIconLoader.LoadSmallPortraits(vpk, heroes);
        Check(portraits.Count == heroes.Count(hero => hero.IconVpkPath != null),
            "Not all available small hero portraits could be decoded.");
        Check(portraits.Values.All(png => png.Length > 8 &&
              png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4e && png[3] == 0x47),
            "A hero portrait was not decoded to PNG.");
    }

    [Fact]
    public void VerifiesCompiledAg2ReferencesOfVindicta()
    {
        var vpk = RequireGameVpk();
        var heroBytes = VpkArchive.ReadFile(vpk, "models/heroes_staging/hornet_v3/hornet.vmdl_c");
        Check(heroBytes is { Length: > 0 }, "Could not extract Vindicta model from Deadlock VPK.");
        using (var resource = new Resource())
        {
            resource.Read(new MemoryStream(heroBytes!));
            var text = resource.DataBlock?.ToString() ?? string.Empty;
            Check(text.Contains("m_animGraph2Refs") && text.Contains("m_vecNmSkeletonRefs"),
                "Vindicta compiled model did not expose expected AG2 fields.");
        }
        var heroFile = _temp["hornet.vmdl_c"];
        File.WriteAllBytes(heroFile, heroBytes!);
        var preset = HeroDatabase.GetVisiblePresets()["vindicta"];
        var error = CsWinCompiler.VerifyCompiledAg2References(heroFile, preset.Skel, preset.Graph, preset.UiGraph);
        Check(error == null, "Compiled AG2 verification rejected Vindicta: " + error);
        var missingGraph = CsWinCompiler.VerifyCompiledAg2References(
            heroFile, preset.Skel, "animgraphs/does_not_exist.vnmgraph", preset.UiGraph);
        Check(missingGraph?.Contains("DefaultAnimGraph2") == true, "Compiled AG2 verification accepted a missing graph.");
    }

    [Fact]
    public void NewHeroPresetsMatchTheGame()
    {
        var vpk = RequireGameVpk();
        using var archive = VpkArchive.Open(vpk);
        foreach (var (heroKey, presetKey) in PresetTests.NewHeroPresets)
        {
            var hero = DeadlockHeroCatalog.GetHeroes().Single(candidate => candidate.HeroKey == heroKey);
            var preset = HeroDatabase.GetVisiblePresets()[presetKey];
            Check(archive.Contains(preset.Skel + "_c") && archive.Contains(preset.UiGraph + "_c"),
                $"Missing skeleton or UI graph in the VPK for {hero.DisplayName}.");
            var compiledFile = _temp[presetKey + ".vmdl_c"];
            File.WriteAllBytes(compiledFile, VpkArchive.ReadFile(vpk, hero.VpkPath)!);
            var error = CsWinCompiler.VerifyCompiledAg2References(compiledFile, preset.Skel, preset.Graph, preset.UiGraph);
            Check(error == null, $"The {hero.DisplayName} preset differs from the game's actual AG2 references: {error}");
        }
    }

    [Fact]
    public void NeutralPresetsMatchTheGame()
    {
        var vpk = RequireGameVpk();
        using var archive = VpkArchive.Open(vpk);
        var presets = HeroDatabase.GetVisiblePresets();
        foreach (var (key, modelPath) in NeutralModels())
        {
            var preset = presets[key];
            Check(archive.Contains(modelPath) && archive.Contains(preset.Skel + "_c") &&
                  preset.NamedGraphs.Values.All(graph => archive.Contains(graph + "_c")) &&
                  (preset.Graph.Length == 0 || archive.Contains(preset.Graph + "_c")),
                $"A preset references an unavailable compiled AG2 resource for {key}.");
            var neutralFile = _temp[key + ".vmdl_c"];
            File.WriteAllBytes(neutralFile, VpkArchive.ReadFile(vpk, modelPath)!);
            var error = CsWinCompiler.VerifyCompiledAg2References(neutralFile, preset.Skel,
                preset.Graph.Length == 0 ? null : preset.Graph, null, preset.NamedGraphs);
            Check(error == null, $"The {key} preset disagrees with actual compiled AG2 bindings: {error}");
            var wrongIdentifier = CsWinCompiler.VerifyCompiledAg2References(neutralFile, null, null, null,
                new Dictionary<string, string> { ["WrongIdentifier"] = preset.NamedGraphs["Neutrals"] });
            Check(wrongIdentifier?.Contains("WrongIdentifier") == true,
                $"Compiled AG2 verification accepted a graph under the wrong identifier for {key}.");
        }
    }

    [Fact]
    public void ReadsTheGameModelForSync()
    {
        var vpk = RequireGameVpk();
        var text = GameModelSource.LoadVmdl(vpk, ["models/heroes_wip/abrams/abrams.vmdl_c"], out var used);
        Check(text != null && used == "models/heroes_wip/abrams/abrams.vmdl_c" &&
              text.Contains("_class = \"AttachmentList\"") && text.Contains("standing_pivot"),
            "The game's Abrams model could not be decompiled for sync from game.");
    }
}
