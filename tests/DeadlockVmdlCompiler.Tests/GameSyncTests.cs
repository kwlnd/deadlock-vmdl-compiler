using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class GameSyncTests : IDisposable
{
    private static readonly GameSync.GameSyncOptions FrameOnly = new(true, false, false);
    private readonly TempDir _temp = new();
    private string ModelFile => _temp["model.vmdl"];

    public void Dispose() => _temp.Dispose();

    private static string AttachmentNode(string name, string bone, string origin, string angles) =>
        "\t\t\t{\n\t\t\t\t_class = \"Attachment\"\n\t\t\t\tname = \"" + name + "\"\n\t\t\t\tparent_bone = \"" + bone +
        "\"\n\t\t\t\trelative_origin = [ " + origin + " ]\n\t\t\t\trelative_angles = [ " + angles +
        " ]\n\t\t\t\tweight = 1.0\n\t\t\t}";

    private static string AttachmentModel(params string[] attachments) =>
        Header + "\n{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n" +
        "\t\t{\n\t\t_class = \"AttachmentList\"\n\t\tchildren =\n\t\t[\n" + string.Join(",\n", attachments) +
        "\n\t\t]\n\t\t}\n\t\t]\n\t}\n}";

    // Values from a September 2026 Abrams export; the expected ones come from the October game files.
    private static readonly string LegacyAttachments = AttachmentModel(
        AttachmentNode("standing_pivot", "root_motion", "0.000015, 103.679985, 0.0", "-89.999992, -89.999992, 0.0"),
        AttachmentNode("crouching_pivot", "root_motion", "0.000015, 71.279984, 0.0", "-89.999992, -89.999992, 0.0"),
        AttachmentNode("far_02", "root_motion", "-43.199989, 15.120007, 26.999989", "-0.0, 89.999992, 179.999878"),
        AttachmentNode("portrait_camera", "root_motion", "-167.575317, -81.296143, 75.108002", "14.442259, 44.221588, -78.229736"),
        AttachmentNode("ability_cast", "hand_R", "1.0, 2.0, 3.0", "-90.0, -90.0, 0.0"));

    private async Task<string> MigrateLegacyAsync()
    {
        File.WriteAllText(ModelFile, LegacyAttachments);
        var migrated = await GameSync.SyncFromGameAsync(ModelFile, FrameOnly, null, null, createBackup: false);
        Check(migrated.Success && migrated.Changed, "Legacy root_motion attachments were not migrated.");
        return File.ReadAllText(ModelFile);
    }

    [Fact]
    public async Task MigratesRootAttachmentsToTheCurrentFrame()
    {
        var migratedText = await MigrateLegacyAsync();
        double[] Field(string name, string field) => Regex.Match(migratedText,
                "name = \"" + name + "\"[^}]*?" + field + @" = \[([^\]]*)\]").Groups[1].Value
            .Split(',').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        bool Near(double[] actual, params double[] expected) =>
            actual.Length == expected.Length && actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 0.001);
        Check(Near(Field("standing_pivot", "relative_origin"), 0, 0, 103.679985) &&
              Near(Field("standing_pivot", "relative_angles"), 0, 0, 0) &&
              Near(Field("far_02", "relative_origin"), 26.999985, -43.199986, 15.12001) &&
              Near(Field("far_02", "relative_angles"), -89.999992, 0, 0) &&
              Near(Field("portrait_camera", "relative_origin"), 75.107979, -167.575302, -81.29615) &&
              Near(Field("portrait_camera", "relative_angles"), -42.484917, 109.766968, -1.872607) &&
              Near(Field("ability_cast", "relative_origin"), 1, 2, 3) &&
              Near(Field("ability_cast", "relative_angles"), -90, -90, 0),
            "Root attachments do not match the current game frame, or a hand attachment was changed.");
        var repeated = await GameSync.SyncFromGameAsync(ModelFile, FrameOnly, null, null, createBackup: false);
        Check(repeated.Success && !repeated.Changed && File.ReadAllText(ModelFile) == migratedText,
            "Fixing current-frame attachments is not idempotent.");
    }

    [Fact]
    public async Task RejectsMixedPivotFrames()
    {
        File.WriteAllText(ModelFile, LegacyAttachments.Replace(
            "71.279984, 0.0 ]\n\t\t\t\trelative_angles = [ -89.999992, -89.999992, 0.0 ]",
            "71.279984, 0.0 ]\n\t\t\t\trelative_angles = [ 0.0, 0.0, 0.0 ]"));
        var mixed = await GameSync.SyncFromGameAsync(ModelFile, FrameOnly, null, null, createBackup: false);
        Check(!mixed.Success && !mixed.Changed, "Mixed camera pivot frames were migrated.");
    }

    [Fact]
    public async Task DetectsTheLegacyFrame()
    {
        var migratedText = await MigrateLegacyAsync();
        File.WriteAllText(ModelFile, LegacyAttachments);
        Check(await GameSync.UsesLegacyCameraFrameAsync(ModelFile), "A legacy camera frame was not detected.");
        File.WriteAllText(ModelFile, migratedText);
        Check(!await GameSync.UsesLegacyCameraFrameAsync(ModelFile), "A current camera frame was reported as legacy.");
        // KV3 permits a trailing comma and comments inside arrays.
        File.WriteAllText(ModelFile, LegacyAttachments.Replace("-89.999992, -89.999992, 0.0 ]", "-89.999992, -89.999992, 0.0, /* old */ ]"));
        Check(await GameSync.UsesLegacyCameraFrameAsync(ModelFile), "A legacy frame with a trailing comma was not detected.");
    }

    [Fact]
    public async Task LeavesModelsWithoutPivotsAlone()
    {
        var neutral = AttachmentModel(AttachmentNode("fx", "root_motion", "1.0, 2.0, 3.0", "-90.0, -90.0, 0.0"));
        File.WriteAllText(ModelFile, neutral);
        var result = await GameSync.SyncFromGameAsync(ModelFile, FrameOnly, null, null, createBackup: false);
        Check(result.Success && !result.Changed && File.ReadAllText(ModelFile) == neutral &&
              !await GameSync.UsesLegacyCameraFrameAsync(ModelFile),
            "A model without camera pivots was changed or rejected.");
    }

    [Fact]
    public async Task KeepsCompactNodesValid()
    {
        // A compact node without relative_angles gets the field on its own whitespace-indented line.
        var compact = Header + "\n{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n" +
            "\t\t\t{ _class = \"Attachment\" name = \"standing_pivot\" parent_bone = \"root_motion\" relative_origin = [ 0.0, 90.0, 0.0 ] relative_angles = [ -90.0, -90.0, 0.0 ] },\n" +
            "\t\t\t{ _class = \"Attachment\" name = \"crouching_pivot\" parent_bone = \"root_motion\" relative_origin = [ 0.0, 60.0, 0.0 ] relative_angles = [ -90.0, -90.0, 0.0 ] },\n" +
            "\t\t\t{ _class = \"Attachment\" name = \"aim\" parent_bone = \"root_motion\" relative_origin = [ 1.0, 2.0, 3.0 ] }\n" +
            "\t\t]\n\t}\n}";
        File.WriteAllText(ModelFile, compact);
        var result = await GameSync.SyncFromGameAsync(ModelFile, FrameOnly, null, null, createBackup: false);
        var text = File.ReadAllText(ModelFile);
        Check(result.Success && result.Changed && Count(text, "{") == Count(compact, "{") &&
              text.Contains("\n\t\t\t\trelative_angles = [ ") && text.Contains("relative_origin = [ 3.0, 1.0, 2.0 ]"),
            "Migrating a compact attachment broke its braces or missed the inserted rotation.");
    }

    [Fact]
    public async Task BatchContinuesPastAFailingModel()
    {
        var migratedText = await MigrateLegacyAsync();
        var legacyModel = _temp["legacy.vmdl"];
        var lockedModel = _temp["locked.vmdl"];
        var currentModel = _temp["current.vmdl"];
        File.WriteAllText(legacyModel, LegacyAttachments);
        File.WriteAllText(lockedModel, LegacyAttachments);
        File.WriteAllText(currentModel, migratedText);
        GameSync.BatchSyncResult batch;
        using (new FileStream(lockedModel, FileMode.Open, FileAccess.Read, FileShare.None))
            batch = await GameSync.SyncAllAsync(new[]
            {
                ("a", new DiscoveredModel { FullPath = lockedModel }),
                ("a", new DiscoveredModel { FullPath = legacyModel }),
                ("b", new DiscoveredModel { FullPath = currentModel })
            }, FrameOnly, null);
        Check(batch.Synced == 1 && batch.Unchanged == 1 && batch.Failed.SequenceEqual(new[] { "a/locked.vmdl" }) &&
              !await GameSync.UsesLegacyCameraFrameAsync(legacyModel),
            "Batch sync stopped at a failing model or miscounted its results.");
    }

    [Fact]
    public void ResolvesVariantPresetKeysToTheirGameModel()
    {
        HeroDatabase.UseBuiltInDatabase();
        Check(GameSync.FallbackGamePath("fencer") == "models/heroes_wip/fencer/fencer.vmdl_c",
            "A preset key that differs from its hero key did not resolve to the game model.");
    }

    [Fact]
    public void CopiesSelectedGroupsFromTheGameModel()
    {
        static string GameData(string gameClass, string keys) =>
            "\t\t\t{\n\t\t\t\t_class = \"GenericGameData\"\n\t\t\t\tgame_class = \"" + gameClass +
            "\"\n\t\t\t\tgame_keys = \n\t\t\t\t{\n\t\t\t\t\t" + keys + "\n\t\t\t\t}\n\t\t\t}";
        static string Model(string attachments, string gameData) =>
            Header + "\n{\n\trootNode =\n\t{\n\t\t_class = \"RootNode\"\n\t\tchildren =\n\t\t[\n" +
            "\t\t{\n\t\t_class = \"AttachmentList\"\n\t\tchildren =\n\t\t[\n" + attachments + "\n\t\t]\n\t\t},\n" +
            "\t\t{\n\t\t_class = \"GameDataList\"\n\t\tchildren =\n\t\t[\n" + gameData + "\n\t\t]\n\t\t},\n" +
            "\t\t]\n\t}\n}";
        var authored = Model(
            AttachmentNode("standing_pivot", "root_motion", "0.0, 0.0, 103.68", "0.0, 0.0, 0.0") + ",\n" +
            AttachmentNode("ability_cast", "hand_R", "9.0, 9.0, 9.0", "0.0, 0.0, 0.0"),
            string.Join(",\n", GameData("CitadelCameraSettings_t", "m_flCameraHeightStanding = 90.0"),
                GameData("CitadelTaggedSoundSettings_t", "m_nOld = 1"),
                GameData("particle_cfg", "m_custom = \"skin\"")));
        var game = Model(
            AttachmentNode("standing_pivot", "root_motion", "0.0, 0.0, 103.68", "0.0, 0.0, 0.0") + ",\n" +
            AttachmentNode("ability_cast", "hand_R", "0.0, 0.0, 0.0", "0.0, 0.0, 0.0") + ",\n" +
            AttachmentNode("healthbar", "root_motion", "0.0, 0.0, 120.0", "0.0, 0.0, 0.0"),
            string.Join(",\n", GameData("CitadelCameraSettings_t", "m_flCameraHeightStanding = 100.0"),
                GameData("TaggedSoundSettings_t", "m_nNew = 1"),
                GameData("particle_cfg", "m_custom = \"game\"")));
        var everything = new GameSync.GameSyncOptions(false, true, true);
        var (synced, _, syncError) = GameSync.SyncFromGameContent(authored, game, everything);
        Check(syncError == null &&
              Count(synced, "name = \"healthbar\"") == 1 && synced.Contains("relative_origin = [ 9.0, 9.0, 9.0 ]") &&
              synced.Contains("m_flCameraHeightStanding = 100.0") && !synced.Contains("m_flCameraHeightStanding = 90.0") &&
              synced.Contains("TaggedSoundSettings_t") && !synced.Contains("CitadelTaggedSoundSettings_t") &&
              synced.Contains("m_custom = \"skin\"") && !synced.Contains("m_custom = \"game\""),
            "Game sync did not add missing data, kept stale data, or replaced authored attachments or particle configs.");
        Check(GameSync.SyncFromGameContent(synced, game, everything).Content == synced, "Game sync is not idempotent.");
        var attachmentsOnly = GameSync.SyncFromGameContent(authored, game, new GameSync.GameSyncOptions(false, true, false)).Content;
        Check(attachmentsOnly.Contains("m_flCameraHeightStanding = 90.0") && attachmentsOnly.Contains("healthbar"),
            "Game sync changed groups that were not selected.");
    }
}
