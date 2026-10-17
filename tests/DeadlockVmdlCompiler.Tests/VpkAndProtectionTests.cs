using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class VpkAndProtectionTests : IDisposable
{
    private static readonly byte[] CompiledBytes = [0x11, 0x22, 0x33, 0x44];
    private readonly TempDir _temp = new();
    private readonly string _gameDir;
    private readonly string _modelDir;
    private readonly string _deployedModel;
    private readonly string _compilerOutput;

    public VpkAndProtectionTests()
    {
        _gameDir = _temp["game"];
        _modelDir = Path.Combine(_gameDir, "models", "heroes_staging", "demo");
        Directory.CreateDirectory(_modelDir);
        _deployedModel = Path.Combine(_modelDir, "protected.vmdl_c");
        _compilerOutput = _temp["compiler-output.vmdl_c"];
        File.WriteAllBytes(_deployedModel, CompiledBytes);
        File.WriteAllBytes(_compilerOutput, CompiledBytes);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task ProtectedModelStaysReadableButNotWritable()
    {
        using (CompiledModelProtection.Acquire(_deployedModel, _compilerOutput))
        {
            Check(File.ReadAllBytes(_deployedModel).SequenceEqual(CompiledBytes), "Protected model could not be read while packaging.");
            var protectedVpk = _temp["protected_pak01_dir.vpk"];
            Check((await VpkBuilder.PackAddonToVpkAsync(_gameDir, protectedVpk)).Success, "VPK packaging could not read a protected model.");
            var protectedBytes = VpkArchive.ReadFile(protectedVpk, "models/heroes_staging/demo/protected.vmdl_c");
            Check(protectedBytes != null && protectedBytes.SequenceEqual(CompiledBytes), "Protected model changed while being packaged.");
            Check(IsWriteBlocked(() =>
            {
                using var stream = new FileStream(_deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.WriteByte(0xFF);
            }), "FileStream write was allowed while the model was protected.");
            Check(IsWriteBlocked(() => File.WriteAllBytes(_deployedModel, [0xFF])),
                "File.WriteAllBytes was allowed while the model was protected.");
            var replacement = _temp["replacement.vmdl_c"];
            File.WriteAllBytes(replacement, [0xFF]);
            Check(IsWriteBlocked(() => File.Move(replacement, _deployedModel, overwrite: true)),
                "Atomic replacement was allowed while the model was protected.");
        }

        File.WriteAllBytes(_compilerOutput, [0x99, 0x22, 0x33, 0x44]);
        Assert.Throws<InvalidDataException>(() => CompiledModelProtection.Acquire(_deployedModel, _compilerOutput).Dispose());
    }

    [Fact]
    public async Task RegistryProtectsVerifiesAndReleasesByAddon()
    {
        using var registry = new ProtectedModelRegistry(_ => { });
        var changes = 0;
        registry.Changed += () => changes++;
        var expectation = new Ag2Expectation("a.vnmskel", null, null, null);
        registry.Protect("source.vmdl", _deployedModel, _compilerOutput, expectation);
        Check(registry.Count == 1 && registry.ExpectedFor("SOURCE.vmdl") == expectation && registry.AnyInAddon(_gameDir),
            "A protected model was not registered with its expectation.");
        Check(IsWriteBlocked(() => File.WriteAllBytes(_deployedModel, [0xFF])), "A registered model was not protected.");
        var registryVpk = _temp["registry_pak01_dir.vpk"];
        Check((await VpkBuilder.PackAddonToVpkAsync(_gameDir, registryVpk)).Success &&
              registry.VerifyPacked(registryVpk, _gameDir, _deployedModel) == null,
            "A correctly packed protected model failed verification.");
        registry.ReleaseForAddon(_gameDir);
        Check(registry.Count == 0 && changes == 2 && !IsWriteBlocked(() =>
        {
            using var stream = new FileStream(_deployedModel, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }), "Releasing an addon did not free its protected models.");
    }

    [Fact]
    public void CompileExpectationsFallBackToDefaults()
    {
        var defaults = Ag2Expectation.ForCompile("models/x.vmdl", " ", "", "ui.vnmgraph", addSkel: true, addGraph: true,
            addUiGraph: false, namedGraphs: new Dictionary<string, string> { ["Neutrals"] = "n.vnmgraph" });
        Check(defaults.Skel == "" && defaults.Graph == null && defaults.UiGraph == null && defaults.NamedGraphs!.Count == 1,
            "Compile expectations did not fall back to defaults or let named graphs replace the default graph.");
    }

    [Fact]
    public async Task PackedEntriesRoundTripIncludingNonAsciiPaths()
    {
        var original = new byte[] { 0x56, 0x50, 0x4b, 0x21 };
        File.WriteAllBytes(Path.Combine(_modelDir, "demo.vmdl_c"), original);
        var vpkPath = _temp["pak01_dir.vpk"];
        var packed = await VpkBuilder.PackAddonToVpkAsync(_gameDir, vpkPath);
        Check(packed.Success, "VPK packing failed: " + packed.Message);
        Check(VpkArchive.ReadFile(vpkPath, "models/heroes_staging/demo/demo.vmdl_c")?.SequenceEqual(original) == true,
            "Embedded VPK entry did not round-trip.");

        var unicodeDir = _temp["unicode_game"];
        Directory.CreateDirectory(Path.Combine(unicodeDir, "models", "модель"));
        File.WriteAllBytes(Path.Combine(unicodeDir, "models", "модель", "тест.vmdl_c"), original);
        var unicodeVpk = _temp["unicode_dir.vpk"];
        File.WriteAllText(unicodeVpk, "previous archive");
        Check((await VpkBuilder.PackAddonToVpkAsync(unicodeDir, unicodeVpk)).Success &&
              VpkArchive.ReadFile(unicodeVpk, "models/модель/тест.vmdl_c")?.SequenceEqual(original) == true &&
              !Directory.EnumerateFiles(_temp.Path, "*.tmp").Any(),
            "A non-ASCII path did not round-trip through the VPK, or a temporary archive was left behind.");
    }
}
