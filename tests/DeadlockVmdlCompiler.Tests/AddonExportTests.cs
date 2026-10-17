using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

/// <summary>
/// Full addon exports from the installed game; slow. Set DEADLOCK_TEST_VPK and DEADLOCK_TEST_EXPORT=1.
/// </summary>
public class AddonExportTests : IDisposable
{
    private readonly TempDir _temp = new();

    public AddonExportTests() => HeroDatabase.UseBuiltInDatabase();

    public void Dispose() => _temp.Dispose();

    private (string ContentAddons, string GameAddons) CreateCsdk(string name)
    {
        var contentAddons = _temp[Path.Combine(name, "content", "citadel_addons")];
        var gameAddons = _temp[Path.Combine(name, "game", "citadel_addons")];
        Directory.CreateDirectory(contentAddons);
        Directory.CreateDirectory(gameAddons);
        return (contentAddons, gameAddons);
    }

    [Fact]
    public async Task ExportsTheNewHeroesWithAllDependencies()
    {
        var vpk = RequireGameVpk();
        RequireFlag("DEADLOCK_TEST_EXPORT");
        var (contentAddons, _) = CreateCsdk("new_hero_csdk12");
        foreach (var (heroKey, presetKey) in PresetTests.NewHeroPresets)
        {
            var hero = DeadlockHeroCatalog.GetHeroes().Single(candidate => candidate.HeroKey == heroKey);
            var addon = await AddonCreationService.CreateAsync(contentAddons, vpk, hero, presetKey + "_export_test");
            Check(File.Exists(addon.MainVmdlPath) &&
                  Path.GetRelativePath(addon.ContentDirectory, addon.MainVmdlPath).Replace('\\', '/') == hero.VpkPath[..^2],
                $"The {hero.DisplayName} export lost the main model or its VPK-relative path.");
            Check(Directory.Exists(addon.GameDirectory) &&
                  VmdlScanner.ScanAddons(contentAddons).Any(candidate => candidate.Name == addon.Name),
                $"The {hero.DisplayName} addon is absent from the CSDK12 or application list.");
            var modelText = File.ReadAllText(addon.MainVmdlPath);
            var meshPaths = Regex.Matches(modelText, @"_class\s*=\s*""RenderMeshFile""[^}]*?\bfilename\s*=\s*""([^""]+\.dmx)""")
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
            var animationFiles = AnimationNodes.AutoDisableAnimationNodesForCompilation(modelText,
                Path.GetDirectoryName(addon.MainVmdlPath)!, Path.GetDirectoryName(addon.MainVmdlPath)!,
                addon.ContentDirectory, addon.ContentDirectory);
            Check(animationFiles.MissingCount == 0,
                $"Automatic detection reports missing animation sources in the {hero.DisplayName} export.");
            var injected = CsWinCompiler.UpgradeVmdlContent(modelText, preset.Skel, preset.Graph, preset.UiGraph);
            Check(injected.Error == null &&
                  injected.Content.Contains(preset.Skel, StringComparison.Ordinal) &&
                  injected.Content.Contains(preset.Graph, StringComparison.Ordinal) &&
                  injected.Content.Contains(preset.UiGraph, StringComparison.Ordinal),
                $"The {hero.DisplayName} export cannot receive its AG2 nodes for CSWin64.");
        }
    }

    [Fact]
    public async Task ExportsWraithWithClothAndRejectsDuplicatesAndCancellation()
    {
        var vpk = RequireGameVpk();
        RequireFlag("DEADLOCK_TEST_EXPORT");
        var (contentAddons, gameAddons) = CreateCsdk("test_csdk12");
        var hero = DeadlockHeroCatalog.GetHeroes().Single(h => h.HeroKey == "wraith");
        var addon = await AddonCreationService.CreateAsync(contentAddons, vpk, hero, "wraith_export_test");
        Check(File.Exists(addon.MainVmdlPath), "Main hero ModelDoc was not exported.");
        Check(File.Exists(Path.Combine(addon.ContentDirectory, "models", "heroes_wip", "wraith", "wraith.vmdl")),
            "Main model lost its VPK-relative path.");
        Check(Directory.Exists(addon.GameDirectory), "CSDK12 game addon directory is missing.");
        var modelDmx = Directory.EnumerateFiles(addon.ContentDirectory, "*model.dmx", SearchOption.AllDirectories).FirstOrDefault();
        Check(modelDmx != null, "Main model mesh DMX was not exported.");
        var dmxText = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(modelDmx!));
        var materialPaths = Regex.Matches(dmxText, @"[A-Za-z0-9_./-]+\.vmat\b").Select(m => m.Value).Distinct().ToArray();
        Check(materialPaths.Length > 0 && materialPaths.All(path =>
                File.Exists(Path.Combine(addon.ContentDirectory, path.Replace('/', Path.DirectorySeparatorChar)))),
            "A referenced material is missing or lost its VPK-relative path.");
        Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.vmat", SearchOption.AllDirectories).Any(),
            "Material dependencies were not exported.");
        Check(Directory.EnumerateFiles(addon.ContentDirectory, "*.png", SearchOption.AllDirectories).Any(),
            "Material texture dependencies were not exported.");
        // Upstream VRF exports only the enabled cloth proxy; older builds also wrote a disabled grid.
        Check(addon.ClothFileCount >= 1, "The cloth proxy was not exported.");
        Check(VmdlScanner.ScanAddons(contentAddons).Any(a => a.Name == "wraith_export_test"),
            "New addon is absent from the application list.");
        await Assert.ThrowsAsync<IOException>(() => AddonCreationService.CreateAsync(contentAddons, vpk, hero, "wraith_export_test"));
        Check(!Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
              !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
            "Temporary addon directories remained after export.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AddonCreationService.CreateAsync(
            contentAddons, vpk, hero, "cancelled_export_test", cancellationToken: cancellation.Token));
        Check(!Directory.Exists(Path.Combine(contentAddons, "cancelled_export_test")) &&
              !Directory.Exists(Path.Combine(gameAddons, "cancelled_export_test")) &&
              !Directory.EnumerateDirectories(contentAddons, ".creating-*").Any() &&
              !Directory.EnumerateDirectories(gameAddons, ".creating-*").Any(),
            "Cancelled export left an addon or temporary directories.");

        var neutral = DeadlockHeroCatalog.GetNeutrals().Single(model => model.HeroKey == "neutral_mushroom_small_01");
        var neutralExport = await AddonCreationService.CreateAsync(contentAddons, vpk, neutral, "neutral_export_test");
        var exportedNeutral = VmdlScanner.ScanAddons(contentAddons).Single(candidate => candidate.Name == "neutral_export_test").HeroModels;
        Check(File.Exists(neutralExport.MainVmdlPath) &&
              exportedNeutral.Any(model => model.FullPath == neutralExport.MainVmdlPath && model.Hero == neutral.HeroKey),
            "An exported neutral is missing its main model or is not selectable afterwards.");
    }
}
