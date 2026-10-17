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

        Check(CsWinCompiler.SyncMaterialTextures(csWinModel, csWinAddon, csdkAddon) == 3,
            "Not every resolvable texture was copied to CSWin64.");
        string Read(string relative) => File.ReadAllText(Path.Combine(csWinAddon, relative));
        Check(Read("materials/default/default_ao.tga") == "citadel" && Read("materials/default/default.tga") == "core" &&
              Read("materials/default/addon_mask.png") == "addon",
            "A texture was taken from the wrong CSDK12 folder.");
        Check(CsWinCompiler.SyncMaterialTextures(csWinModel, csWinAddon, csdkAddon) == 0,
            "Textures already in CSWin64 were copied again.");
    }

    [Fact]
    public void SummarizesALongCompilerFailure()
    {
        var lines = Enumerable.Range(0, 40).Select(i => i % 4 == 0 ? $"Error loading texture {i}" : $"Read input {i}").ToList();
        var summary = CsWinCompiler.SummarizeCompilerErrors(lines);
        Check(summary.Split('\n').Length == 9 && summary.StartsWith("Error loading texture 0") &&
              !summary.Contains("Read input") && summary.Contains("2 more line(s)"),
            "A failed compile should show a few error lines and point to the log.");
    }
}
