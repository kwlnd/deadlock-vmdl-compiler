using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class CompilerSyncTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void CopiesTexturesMaterialsReferenceFromOutsideTheModelFolder()
    {
        var csdkContent = _temp["csdk/content"];
        var csdkAddon = Path.Combine(csdkContent, "citadel_addons", "mod");
        var csWinAddon = _temp["cswin/content/csgo_addons/mod"];
        var csWinModel = Path.Combine(csWinAddon, "models", "hero");
        Write(Path.Combine(csdkAddon, "materials", "default", "addon_mask.png"), "addon");
        Write(Path.Combine(csdkContent, "citadel", "materials", "default", "default_ao.tga"), "citadel");
        Write(Path.Combine(csdkContent, "core", "materials", "default", "default.tga"), "core");
        // Not named by the material: a shader can still fall back to it.
        Write(Path.Combine(csdkContent, "citadel", "materials", "default", "default_alt.tga"), "fallback");
        Write(Path.Combine(csWinModel, "materials", "body.vmat"), """
            "Layer0"
            {
                "TextureAmbientOcclusion1"	"materials/default/default_ao.tga"
                "TextureColor1"	"materials/default/default.tga"
                "TextureRimLightMask1"	"materials/default/addon_mask.png"
                "TextureRoughness1"	"[1.000000 1.000000 1.000000 0.000000]"
                "TextureNormal1"	"materials/missing/nowhere.tga"
            }
            """);

        Check(CsWinCompiler.SyncMaterialTextures(csWinModel, csWinAddon, csdkAddon) == 4,
            "Not every resolvable texture was copied to CSWin64.");
        string Read(string relative) => File.ReadAllText(Path.Combine(csWinAddon, relative));
        Check(Read("materials/default/default_ao.tga") == "citadel" && Read("materials/default/default.tga") == "core" &&
              Read("materials/default/addon_mask.png") == "addon" && Read("materials/default/default_alt.tga") == "fallback",
            "A texture was taken from the wrong CSDK12 folder.");
        Check(CsWinCompiler.SyncMaterialTextures(csWinModel, csWinAddon, csdkAddon) == 0,
            "Textures already in CSWin64 were copied again.");

        // An edited texture must reach CSWin64 on the next compile.
        var edited = Path.Combine(csdkAddon, "materials", "default", "addon_mask.png");
        File.WriteAllText(edited, "addon v2");
        File.SetLastWriteTimeUtc(edited, DateTime.UtcNow.AddMinutes(1));
        Check(CsWinCompiler.SyncMaterialTextures(csWinModel, csWinAddon, csdkAddon) == 1 &&
              Read("materials/default/addon_mask.png") == "addon v2",
            "An edited texture outside the model folder was not copied to CSWin64 again.");
    }

    [Fact]
    public void CopiesMaterialsTheModelUsesFromOtherFolders()
    {
        var csdkAddon = _temp["csdk/content/citadel_addons/walker"];
        var csWinAddon = _temp["cswin/content/csgo_addons/walker"];
        Write(Path.Combine(csdkAddon, "models", "npc", "walker", "materials", "body.vmat"), "body");
        Write(Path.Combine(csdkAddon, "models", "npc", "walker", "materials", "body_friendly.vmat"), "friendly");
        Write(Path.Combine(csdkAddon, "models", "npc", "walker_v2", "body.dmx"),
            "\0\0models/npc/walker/materials/body.vmat\0\0");
        var vmdl = """
            { rootNode = { _class = "RootNode" children = [
                { _class = "RenderMeshList" children = [
                    { _class = "RenderMeshFile" filename = "models/npc/walker_v2/body.dmx" } ] },
                { _class = "MaterialGroupList" children = [
                    { _class = "MaterialGroup" remaps = [
                        { from = "models/npc/walker/materials/body.vmat" to = "models/npc/walker/materials/body_friendly.vmat" },
                        { from = "materials/stock/game_only.vmat" to = "materials/stock/game_only.vmat" } ] } ] } ] } }
            """;

        Check(CsWinCompiler.SyncReferencedMaterials(vmdl, csdkAddon, csWinAddon) == 2,
            "Materials from another folder of the addon were not copied to CSWin64.");
        Check(File.ReadAllText(Path.Combine(csWinAddon, "models", "npc", "walker", "materials", "body_friendly.vmat")) == "friendly",
            "A material group variant was not copied to its own path.");
        Check(CsWinCompiler.SyncReferencedMaterials(vmdl, csdkAddon, csWinAddon) == 0,
            "Unchanged materials were copied again.");
    }

    [Fact]
    public void SummarizesALongCompilerFailure()
    {
        var lines = Enumerable.Range(0, 40).Select(i => i % 4 == 0 ? $"Error loading texture {i}" : $"Read input {i}").ToList();
        var summary = CsWinCompiler.SummarizeCompilerErrors(lines);
        Check(summary.Split('\n').Length == 9 && summary.StartsWith("Error loading texture 0") &&
              !summary.Contains("Read input") && summary.Contains("2 more line(s)"),
            "A failed compile should show a few error lines and point to the log.");
        Check(CsWinCompiler.SummarizeCompilerErrors(lines, @"C:\logs\compilation_error.log")
                .EndsWith(@"2 more line(s); full compiler output: C:\logs\compilation_error.log."),
            "The summary does not say where the full compiler output was saved.");
    }
}
