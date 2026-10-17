using System.Text.Json;
using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Services;

// The presets, the VPK cache and the app log are process-wide, so tests run one at a time.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace DeadlockVmdlCompiler.Tests;

internal static class TestSupport
{
    public const string Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc40:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
    public static readonly string BareModel = Header + "\n{\nrootNode =\n{\n_class = \"RootNode\"\nchildren =\n[\n]\n}\n}";
    public static readonly (string Skel, string Graph, string Ui) Paths = (
        "models/heroes_staging/hornet_v3/hornet.vnmskel",
        "animgraphs/animgraph2/hero/hero.vnmgraph+vindicta.vnmgraph",
        "animgraphs/animgraph2/hero/hero_ui.vnmgraph+vindicta.vnmgraph");

    public static readonly string LooseReferences = Header + "\n{ rootNode = { _class = \"RootNode\" children = [ " +
        "{ _class = \"NmSkeletonReference\" filename = \"loose.vnmskel\" }, " +
        "{ _class = \"Note\" text = \"keep { _class = \\\"AnimGraph2\\\" } here\" }, " +
        "{ _class = \"AnimGraph2List\" children = [ { _class = \"AnimGraph2\" name = \"ui\" filename = \"a.vnmgraph\" } ] } ] } }";

    public static readonly string AnimationModel = Header + """

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

    /// <summary>The model with the default AG2 references injected.</summary>
    public static string InjectedModel => CsWinCompiler.UpgradeVmdlContent(BareModel, Paths.Skel, Paths.Graph, Paths.Ui).Content;

    public static void Check(bool condition, string message) => Assert.True(condition, message);

    public static int Count(string text, string token) => Regex.Matches(text, Regex.Escape(token)).Count;

    public static bool IsWriteBlocked(Action write)
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

    /// <summary>The 19 AG2 neutral models and their VPK paths, from the fixture.</summary>
    public static Dictionary<string, string> NeutralModels()
    {
        using var stream = typeof(TestSupport).Assembly.GetManifestResourceStream("NeutralAg2Models.json");
        Assert.NotNull(stream);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    /// <summary>Path of a real pak01_dir.vpk from DEADLOCK_TEST_VPK, or skips the test.</summary>
    public static string RequireGameVpk()
    {
        var vpk = Environment.GetEnvironmentVariable("DEADLOCK_TEST_VPK");
        if (string.IsNullOrWhiteSpace(vpk) || !File.Exists(vpk))
            Assert.Skip("Set DEADLOCK_TEST_VPK to a real pak01_dir.vpk to run this check.");
        return vpk;
    }

    public static void RequireFlag(string name)
    {
        if (Environment.GetEnvironmentVariable(name) != "1")
            Assert.Skip($"Set {name}=1 to run this check.");
    }
}

/// <summary>A temporary directory removed after the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deadlock-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
