using System.Text.Json;
using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class AnimationTests
{
    [Fact]
    public void ManualToggleOnlyChangesTheAnimationList()
    {
        var sanitized = Ag2Sanitizer.SanitizeVmdlContent(AnimationModel).CleanContent;
        Check(sanitized.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
              sanitized.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true") &&
              sanitized.Contains("_class = \"Folder\" disabled = false"),
            "ModelDoc sanitizer changed disabled flags on children inside an animation node.");
        var withAnimations = AnimationNodes.DisableAnimationNodesForCompilation(sanitized, disableAnimationList: false);
        Check(Regex.IsMatch(withAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
              withAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false") &&
              withAnimations.Contains("_class = \"AnimFile\" name = \"muted_legacy\" disabled = true"),
            "Unchecking disable animations did not enable the exported AnimationList without changing clips.");
        Check(AnimationNodes.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: false) == withAnimations,
            "Enabling the AnimationList is not idempotent.");
        var withoutAnimations = AnimationNodes.DisableAnimationNodesForCompilation(withAnimations, disableAnimationList: true);
        Check(Regex.IsMatch(withoutAnimations, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*true\b") &&
              withoutAnimations.Contains("_class = \"AnimFile\" name = \"active_legacy\" disabled = false"),
            "Checking disable animations did not disable only the AnimationList.");
        Check(AnimationNodes.DisableAnimationNodesForCompilation(withoutAnimations, disableAnimationList: false) == withAnimations,
            "AnimationList state did not round-trip when toggling the checkbox.");
        var withoutFlag = AnimationModel.Replace("_class = \"AnimationList\" disabled = true", "_class = \"AnimationList\"");
        Check(Regex.IsMatch(AnimationNodes.DisableAnimationNodesForCompilation(withoutFlag, disableAnimationList: false),
                @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b"),
            "An AnimationList without an explicit disabled field was not enabled.");
    }

    [Fact]
    public void RealModelKeepsItsClips()
    {
        var realAnimationVmdl = Environment.GetEnvironmentVariable("DEADLOCK_TEST_ANIMATION_VMDL");
        if (string.IsNullOrWhiteSpace(realAnimationVmdl))
            Assert.Skip("Set DEADLOCK_TEST_ANIMATION_VMDL to a real ModelDoc file to run this check.");
        var originalModel = File.ReadAllText(realAnimationVmdl);
        var enabledModel = AnimationNodes.DisableAnimationNodesForCompilation(originalModel, disableAnimationList: false);
        var originalClipCount = Regex.Matches(originalModel, @"_class\s*=\s*""AnimFile""").Count;
        Check(originalClipCount > 0 &&
              Regex.IsMatch(enabledModel, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
              Regex.Matches(enabledModel, @"_class\s*=\s*""AnimFile""").Count == originalClipCount,
            "Real ModelDoc lost animations or kept AnimationList disabled.");
    }

    [Fact]
    public void AutomaticDetectionDisablesOnlyMissingClips()
    {
        using var temp = new TempDir();
        var animationRoot = temp["animation_sources"];
        var animationDirectory = Path.Combine(animationRoot, "models", "demo");
        var winRoot = temp["cswin_animation_sources"];
        var winDirectory = Path.Combine(winRoot, "models", "demo");
        Directory.CreateDirectory(animationDirectory);
        Directory.CreateDirectory(winDirectory);
        File.WriteAllText(Path.Combine(animationDirectory, "active.dmx"), "active source");
        File.WriteAllText(Path.Combine(animationDirectory, "muted.dmx"), "muted source");

        var available = AnimationNodes.AutoDisableAnimationNodesForCompilation(AnimationModel,
            animationDirectory, winDirectory, animationRoot, winRoot);
        Check(available.FoundCount == 2 && available.MissingCount == 0 &&
              Regex.IsMatch(available.Content, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*false\b") &&
              available.Content.Contains("name = \"muted_legacy\" disabled = true", StringComparison.Ordinal),
            "Automatic animation detection kept the list disabled or unmuted an intentional clip.");
        var missingReference = AnimationModel.Replace("models/demo/active.dmx", "models/not_here/active.dmx", StringComparison.Ordinal);
        var wrongNameMatch = AnimationNodes.AutoDisableAnimationNodesForCompilation(missingReference,
            animationDirectory, winDirectory, animationRoot, winRoot);
        Check(wrongNameMatch.FoundCount == 1 && wrongNameMatch.MissingCount == 1,
            "A same-named file elsewhere incorrectly satisfied an animation source path.");
        var allMissing = AnimationNodes.AutoDisableAnimationNodesForCompilation(AnimationModel,
            winDirectory, winDirectory, winRoot, winRoot);
        Check(allMissing.FoundCount == 0 && allMissing.MissingCount == 2 &&
              Regex.IsMatch(allMissing.Content, @"_class\s*=\s*""AnimationList""\s+disabled\s*=\s*true\b") &&
              allMissing.Content.Contains("_class = \"Folder\" disabled = false", StringComparison.Ordinal),
            "Missing-animation handling did not disable the list or changed unrelated nested nodes.");
        var forcedOff = AnimationNodes.PrepareAnimationNodesForCompilation(AnimationModel, true, true,
            animationDirectory, winDirectory, animationRoot, winRoot);
        Check(forcedOff == AnimationNodes.DisableAnimationNodesForCompilation(AnimationModel, true),
            "Automatic mode overrides the manual disable animationlist checkbox.");
        var strictKeep = AnimationNodes.PrepareAnimationNodesForCompilation(missingReference, false, false,
            animationDirectory, winDirectory, animationRoot, winRoot);
        Check(strictKeep == AnimationNodes.DisableAnimationNodesForCompilation(missingReference, false),
            "Turning automatic mode off still disables clips.");
        var relativeAnims = AnimationModel.Replace("models/demo/active.dmx", "active.dmx", StringComparison.Ordinal);
        Check(AnimationNodes.AutoDisableAnimationNodesForCompilation(relativeAnims,
                animationDirectory, winDirectory, animationRoot, winRoot).FoundCount == 2,
            "Model-relative animation paths no longer resolve.");

        var sharedDirectory = Path.Combine(animationRoot, "models", "shared");
        Directory.CreateDirectory(sharedDirectory);
        var sharedBytes = new byte[] { 1, 4, 7, 10 };
        File.WriteAllBytes(Path.Combine(sharedDirectory, "shared.dmx"), sharedBytes);
        var sharedAnims = AnimationModel.Replace("models/demo/active.dmx", "models/shared/shared.dmx", StringComparison.Ordinal);
        Check(AnimationNodes.SynchronizeAnimationSourceFiles(sharedAnims, animationDirectory, animationRoot, winRoot) == 2 &&
              File.ReadAllBytes(Path.Combine(winRoot, "models", "shared", "shared.dmx")).SequenceEqual(sharedBytes),
            "An animation outside the model directory was not synchronized at its exact addon-relative path.");
        var commentAnims = AnimationModel.Replace("name = \"active_legacy\"", "name = \"active_legacy\" description = \"uses {events}\"", StringComparison.Ordinal) +
                           "\n// { _class = \"AnimFile\" source_filename = \"missing.dmx\" }";
        Check(AnimationNodes.AutoDisableAnimationNodesForCompilation(commentAnims,
                animationDirectory, winDirectory, animationRoot, winRoot).FoundCount == 2,
            "Comments or braces inside strings confused automatic animation detection.");
    }

    [Fact]
    public void AnimationAndMaterialSettingsSurviveMigration()
    {
        var oldConfig = JsonSerializer.Deserialize<AppConfig>("{\"chk_disable_anim_list\":true}")!;
        var contributorConfig = JsonSerializer.Deserialize<AppConfig>("{\"chk_auto_detect_anims\":false}")!;
        Check(oldConfig.ChkDisableAnimList && oldConfig.ChkAutoDetectAnims &&
              !contributorConfig.ChkDisableAnimList && !contributorConfig.ChkAutoDetectAnims,
            "Settings from either version lose their animation mode during migration.");
        var materialConfig = JsonSerializer.Deserialize<AppConfig>("{\"chk_skip_material_compilation\":true}")!;
        Check(materialConfig.ChkSkipMaterialCompilation && !new AppConfig().ChkSkipMaterialCompilation,
            "Material compilation setting does not preserve the opt-in default.");
    }
}
