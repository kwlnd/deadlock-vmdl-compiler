using System.Diagnostics;
using System.Text.RegularExpressions;
using ValveResourceFormat;

namespace DeadlockVmdlCompiler.Services;

/// <summary>Compiles a ModelDoc model with CSWin64: AG2 injection, resourcecompiler, verification and deployment.</summary>
public static class CsWinCompiler
{
    public const string ModelDoc41Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
    public const string DefaultCsWinDir = @"A:\modding\CSWin64";

    private sealed class GameInfoMaterialGuard : IDisposable
    {
        private const string SettingName = "content_consider_missing_materials_fatal";
        private readonly string _path;
        private readonly byte[] _originalBytes;
        private readonly Action<string>? _onLog;
        private bool _disposed;

        private GameInfoMaterialGuard(string path, byte[] originalBytes, Action<string>? onLog)
        {
            _path = path;
            _originalBytes = originalBytes;
            _onLog = onLog;
        }

        public static GameInfoMaterialGuard Disable(string cswinDir, Action<string>? onLog)
        {
            var path = FindGameInfoPath(cswinDir);
            if (path == null)
                throw new FileNotFoundException(
                    $"Could not find {SettingName} in the CSWin64 gameinfo files.");

            var originalBytes = File.ReadAllBytes(path);
            var content = File.ReadAllText(path);
            var pattern = new Regex(
                @"^[ \t]*" + Regex.Escape(SettingName) +
                @"\s*\{[^{}]*\}[ \t]*(?:\r?\n)?",
                RegexOptions.Multiline | RegexOptions.Singleline);
            var updated = pattern.Replace(content, string.Empty, 1);
            if (updated == content)
                throw new InvalidDataException(
                    $"The CSWin64 gameinfo file contains {SettingName}, but its block could not be edited: {path}");

            File.WriteAllText(path, updated);
            onLog?.Invoke($"[gameinfo] temporarily disabled {SettingName}: {path}");
            return new GameInfoMaterialGuard(path, originalBytes, onLog);
        }

        private static string? FindGameInfoPath(string cswinDir)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cswinDir));
            var gameRoot = string.Equals(Path.GetFileName(root), "game", StringComparison.OrdinalIgnoreCase)
                ? root
                : Path.Combine(root, "game");
            if (!Directory.Exists(gameRoot)) return null;

            var candidates = new List<string>();
            foreach (var folder in new[] { "csgo_core", "csgo", "csgo_imported" })
            {
                var preferred = Path.Combine(gameRoot, folder, "gameinfo.gi");
                if (File.Exists(preferred)) candidates.Add(preferred);
            }

            try
            {
                candidates.AddRange(Directory.EnumerateFiles(
                    gameRoot, "gameinfo.gi", SearchOption.AllDirectories));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.ReadAllText(candidate).Contains(SettingName, StringComparison.Ordinal))
                        return candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                File.WriteAllBytes(_path, _originalBytes);
                _onLog?.Invoke($"[gameinfo] restored {SettingName}: {_path}");
            }
            catch (Exception ex)
            {
                _onLog?.Invoke($"[gameinfo error] could not restore {_path}: {ex.Message}");
                throw new IOException($"Could not restore the original CSWin64 gameinfo file: {_path}", ex);
            }
        }
    }

    private sealed record CsWinLayout(string CompilerPath, string GameRoot, string ContentRoot);

    private static CsWinLayout? ResolveCsWinLayout(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var compiler = Path.Combine(root, "game", "bin", "win64", "resourcecompiler.exe");
        if (File.Exists(compiler))
            return new CsWinLayout(compiler, Path.Combine(root, "game"), Path.Combine(root, "content"));

        compiler = Path.Combine(root, "bin", "win64", "resourcecompiler.exe");
        var parent = Path.GetDirectoryName(root);
        return File.Exists(compiler) && parent != null
            ? new CsWinLayout(compiler, root, Path.Combine(parent, "content"))
            : null;
    }

    public static bool IsValidCsWinDir(string? path) => ResolveCsWinLayout(path) != null;

    public static ModelDocEdit UpgradeVmdlContent(
        string content,
        string skelPath,
        string graphPath,
        string? uiGraphPath = null,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        bool upgradeHeader = true,
        IReadOnlyDictionary<string, string>? namedGraphs = null)
        => ModelDocAg2Editor.Upgrade(content, skelPath, graphPath, uiGraphPath,
            addSkel, addGraph, addUiGraph, upgradeHeader, ModelDoc41Header, namedGraphs);

    public record CompileProgress(
        int Percent,
        string Stage,
        string Detail
    );

    // CSWin64 has no Deadlock shaders, so every synced .vmat fails to compile there.
    // The model only stores the material path, so this block is expected and harmless.

    // CSWin64 has no Deadlock shaders, so every synced .vmat fails to compile there.
    // The model only stores the material path, so this block is expected and harmless.
    private static bool IsMaterialShaderNoise(string line, ref bool insideMaterialFailure)
    {
        var text = line.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal) && text.EndsWith(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            insideMaterialFailure = true;
            return true;
        }
        if (!insideMaterialFailure) return false;
        if (text == "[FAIL]")
        {
            insideMaterialFailure = false;
            return true;
        }
        if (text.Contains("No valid vcs file found for shader", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LoadVfxAndFeatureCombo", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Feature combo not found", StringComparison.OrdinalIgnoreCase))
            return true;
        insideMaterialFailure = false;
        return false;
    }

    private static bool IsCompilerNoiseLine(string rawLine, out string? cleanedLine)
    {
        cleanedLine = null;
        if (string.IsNullOrWhiteSpace(rawLine))
            return true;

        var line = rawLine.Trim();

        // 1. Missing material references & illegal resource loaders (CSWin64 does not host Deadlock materials)
        if (line.Contains("missing material", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("referencing missing material", StringComparison.OrdinalIgnoreCase) ||
            (line.Contains("Trying to load an illegal resource name", StringComparison.OrdinalIgnoreCase) && line.Contains(".vmat", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 2. Generic warning headers produced when materials are missing
        if (line.Contains("Compile WARNINGS", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("These may represent problems, but will not cause the compile to fail", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Look for \"RESOURCE COMPILE WARNING:\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Device banner, the echoed input path and per-file timing repeat what the summary line says.
        if (line.StartsWith("Creating device for graphics adapter", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Compile of ", StringComparison.OrdinalIgnoreCase) ||
            (line.StartsWith("- ", StringComparison.Ordinal) && line.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(line, @"^\d+/\s*\d+ \(elapsed"))
        {
            return true;
        }

        // 3. Dashed or equal sign horizontal separator lines
        if (line.Length >= 5 && line.All(c => c == '-' || c == '='))
        {
            return true;
        }

        // 4. "RESOURCE COMPILE WARNING:" specifically for .vmat
        if (line.Contains("RESOURCE COMPILE WARNING:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(".vmat", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 5. If summary line has "WARNING: 1 compiled, 0 failed...", strip the "WARNING: " prefix
        if (line.Contains("compiled,", StringComparison.OrdinalIgnoreCase) && line.Contains("failed,", StringComparison.OrdinalIgnoreCase))
        {
            if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            {
                line = line.Substring("WARNING:".Length).Trim();
            }
            cleanedLine = line;
            return false;
        }

        cleanedLine = line;
        return false;
    }

    public static async Task<OperationResult> CompileViaCsWinAndDeployAsync(
        string csdk12VmdlPath,
        string upgradedVmdlContent,
        string? cswinDir = null,
        string? citadelAddonsDir = null,
        bool disableAnimationList = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null,
        string? expectedSkelPath = null,
        string? expectedGraphPath = null,
        string? expectedUiGraphPath = null,
        Action<string>? beforeDeploy = null,
        Action<string, string>? afterDeploy = null,
        bool autoDetectAnims = true,
        IReadOnlyDictionary<string, string>? expectedNamedGraphs = null,
        bool skipMaterialCompilation = false,
        CancellationToken cancellationToken = default,
        List<string>? compilerErrors = null)
    {
        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : cfg.CsWinDir;
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        var layout = ResolveCsWinLayout(useCsWinDir);
        if (layout == null)
            return OperationResult.Fail($"CSWin64 resourcecompiler.exe not found in: {useCsWinDir}");
        var rcExe = layout.CompilerPath;
        var csWinGameDir = Path.Combine(layout.GameRoot, "csgo");

        GameInfoMaterialGuard? gameInfoGuard = null;
        if (skipMaterialCompilation)
        {
            try
            {
                gameInfoGuard = GameInfoMaterialGuard.Disable(useCsWinDir, onLog);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail($"Could not temporarily disable missing-material failures in CSWin64 gameinfo: {ex.Message}");
            }
        }
        using var materialGuard = gameInfoGuard;

        var (_, addonName, subpath) = CsdkPaths.ParseCsdkPath(csdk12VmdlPath, useCitadelDir);

        var csWinAddonRoot = Path.Combine(layout.ContentRoot, "csgo_addons", addonName);
        var csWinVmdlPath = Path.Combine(csWinAddonRoot, subpath);
        var csWinVmdlDir = Path.GetDirectoryName(csWinVmdlPath)!;
        var csWinCompiledVmdlc = Path.Combine(layout.GameRoot, "csgo_addons", addonName, subpath + "_c");
        var csdkVmdlDir = Path.GetDirectoryName(csdk12VmdlPath);
        var normalizedSource = Path.GetFullPath(csdk12VmdlPath).Replace('\\', '/');
        var normalizedSubpath = subpath.Replace('\\', '/');
        var csdkAddonRoot = normalizedSource.EndsWith("/" + normalizedSubpath, StringComparison.OrdinalIgnoreCase)
            ? normalizedSource[..^(normalizedSubpath.Length + 1)].Replace('/', Path.DirectorySeparatorChar)
            : csdkVmdlDir ?? string.Empty;
        Directory.CreateDirectory(csWinVmdlDir);

        // 1. Sync model, material, texture, and animation sources to CSWin64 so
        // the compiler can resolve the authored paths. Material/texture
        // recompilation can be disabled independently with -skiptype below.
        if (!string.IsNullOrEmpty(csdkVmdlDir) && Directory.Exists(csdkVmdlDir))
        {
            progress?.Report(new CompileProgress(25, "[2/5] syncing assets", "scanning model assets..."));

            var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dmx", ".fbx", ".smd", ".obj", ".vmat", ".png", ".vanim"
            };

            var filesToCopy = Directory.EnumerateFiles(csdkVmdlDir, "*.*", SearchOption.AllDirectories)
                .Where(f => allowedExts.Contains(Path.GetExtension(f)))
                .Where(f => NeedsCopy(f, Path.Combine(csWinVmdlDir, Path.GetRelativePath(csdkVmdlDir, f))))
                .ToList();
            // A first sync copies hundreds of clips; list files only when the list is readable.
            var listFiles = filesToCopy.Count <= 20;

            int copied = 0;
            foreach (var srcFile in filesToCopy)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relFile = Path.GetRelativePath(csdkVmdlDir, srcFile);
                var dstFile = Path.Combine(csWinVmdlDir, relFile);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                    File.Copy(srcFile, dstFile, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Compiling against a stale copy would silently ship the previous mesh.
                    return OperationResult.Fail($"Could not sync {relFile} to CSWin64: {ex.Message}");
                }
                copied++;
                progress?.Report(new CompileProgress(25 + (int)(20.0 * copied / Math.Max(1, filesToCopy.Count)),
                    "[2/5] syncing assets",
                    relFile
                ));
                if (listFiles) onLog?.Invoke($"[sync] copied: {relFile}");
            }
            if (copied > 0) onLog?.Invoke($"[sync] copied {copied} new or changed asset(s) to cswin64");

            // Materials also point at textures outside the model folder, such as
            // materials/default/default_ao.tga; CSWin64 fails on a missing one.
            try
            {
                var textures = SyncMaterialTextures(csWinVmdlDir, csWinAddonRoot, csdkAddonRoot);
                if (textures > 0) onLog?.Invoke($"[sync] copied {textures} texture(s) the materials reference from outside the model folder");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail($"Could not sync material textures to CSWin64: {ex.Message}");
            }
        }

        // Animations can live outside the model folder. Preserve their addon-relative
        // paths in CSWin64 before deciding whether a clip is missing.
        if (!disableAnimationList)
        {
            try
            {
                AnimationNodes.SynchronizeAnimationSourceFiles(upgradedVmdlContent,
                    csdkVmdlDir ?? string.Empty, csdkAddonRoot, csWinAddonRoot, onLog);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return OperationResult.Fail($"Could not sync animation source: {ex.Message}");
            }
        }

        // 2. Keep the manual override and the contributor's automatic mode.
        progress?.Report(new CompileProgress(50, "[3/5] preparing modeldoc", "temporary definition..."));
        var csWinContent = AnimationNodes.PrepareAnimationNodesForCompilation(upgradedVmdlContent,
            disableAnimationList, autoDetectAnims, csdkVmdlDir ?? string.Empty, csWinVmdlDir,
            csdkAddonRoot, csWinAddonRoot, onLog);

        await File.WriteAllTextAsync(csWinVmdlPath, csWinContent, cancellationToken);

        // A successful compiler exit must not be mistaken for an old output from a previous run.
        if (File.Exists(csWinCompiledVmdlc))
            File.Delete(csWinCompiledVmdlc);

        var compilerArguments = BuildResourceCompilerArguments(csWinVmdlPath, csWinGameDir,
            skipMaterialCompilation);
        var psi = new ProcessStartInfo
        {
            FileName = rcExe,
            Arguments = compilerArguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        progress?.Report(new CompileProgress(60, "[4/5] compiling model", "resourcecompiler.exe"));
        onLog?.Invoke(skipMaterialCompilation
            ? "[compiler] starting: resourcecompiler.exe with material/texture compilation disabled (-skiptype vmat,vtex)"
            : $"[compiler] starting: resourcecompiler.exe -f -i \"{Path.GetFileName(csWinVmdlPath)}\"");

        var outputLines = new List<string>();
        var errorLines = new List<string>();
        var rawLines = new List<string>();
        var outputLock = new object();
        var insideMaterialFailure = false;
        var hiddenMaterials = 0;

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                lock (outputLock) rawLines.Add(e.Data);
                if (IsMaterialShaderNoise(e.Data, ref insideMaterialFailure))
                {
                    if (e.Data.Trim() == "[FAIL]") hiddenMaterials++;
                    return;
                }
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    lock (outputLock)
                    {
                        // The compiler repeats each warning without its prefix a moment later.
                        if (outputLines.Any(seen => seen.EndsWith(cleaned!, StringComparison.Ordinal))) return;
                        outputLines.Add(cleaned!);
                        if (cleaned!.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                            compilerErrors?.Add(cleaned["ERROR:".Length..].Trim());
                    }
                    progress?.Report(new CompileProgress(75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64] {cleaned!}");
                }
            }
        };
        proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                lock (outputLock) rawLines.Add(e.Data);
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    lock (outputLock) errorLines.Add(cleaned!);
                    progress?.Report(new CompileProgress(75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64 err] {cleaned!}");
                }
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }

        if (hiddenMaterials > 0)
            onLog?.Invoke($"[cswin64] skipped {hiddenMaterials} material(s): CSWin64 has no Deadlock shaders, the model keeps their paths");

        if (proc.ExitCode != 0)
        {
            var all = errorLines.Count > 0 ? errorLines : outputLines.Count > 0 ? outputLines : rawLines;
            return OperationResult.Fail($"CSWin64 Compiler error (code {proc.ExitCode}): {SummarizeCompilerErrors(all)}");
        }

        if (!File.Exists(csWinCompiledVmdlc))
        {
            return OperationResult.Fail($"Compiler finished but .vmdl_c was not created at: {csWinCompiledVmdlc}");
        }

        var verificationError = VerifyCompiledAg2References(csWinCompiledVmdlc,
            expectedSkelPath, expectedGraphPath, expectedUiGraphPath, expectedNamedGraphs);
        if (verificationError != null)
            return OperationResult.Fail(verificationError);

        progress?.Report(new CompileProgress(90, "[5/5] deploying model", Path.GetFileName(csWinCompiledVmdlc)));

        var csdk12GameVmdlc = Path.GetFullPath(Path.Combine(
            CsdkPaths.ResolveGameAddonDir(csdk12VmdlPath, useCitadelDir, addonName), subpath + "_c"));

        Directory.CreateDirectory(Path.GetDirectoryName(csdk12GameVmdlc)!);
        beforeDeploy?.Invoke(csdk12GameVmdlc);
        File.Copy(csWinCompiledVmdlc, csdk12GameVmdlc, overwrite: true);
        afterDeploy?.Invoke(csdk12GameVmdlc, csWinCompiledVmdlc);

        var vmdlcSize = new FileInfo(csdk12GameVmdlc).Length;
        onLog?.Invoke($"[deploy] deployed .vmdl_c ({vmdlcSize / 1024:N0} KB) to: {csdk12GameVmdlc}");

        // Materials, textures and morph data the compile produced must ship with the model:
        // the VPK is packed from the CSDK12 game addon, and a mod's own materials are not in the game.
        try
        {
            var csWinGameAddon = Path.Combine(layout.GameRoot, "csgo_addons", addonName);
            var csdkGameAddon = csdk12GameVmdlc[..^(subpath.Length + 2)];
            var deployed = DeployCompiledDependencies(csWinCompiledVmdlc, csWinGameAddon, csdkGameAddon);
            if (deployed > 0)
                onLog?.Invoke($"[deploy] copied {deployed} compiled material/texture file(s) the model uses");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return OperationResult.Fail($"The model was deployed, but its compiled materials could not be copied: {ex.Message}");
        }

        return OperationResult.Ok($"Compiled via CSWin64 & deployed .vmdl_c to: {csdk12GameVmdlc}");
    }

    /// <summary>
    /// The lines of a failed compile worth showing in a dialog: errors and failures first, a few
    /// of them; the full output is already in the log.
    /// </summary>
    public static string SummarizeCompilerErrors(IReadOnlyList<string> lines)
    {
        const int shown = 8;
        var errors = lines.Where(line => line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                         line.Contains("[FAIL]", StringComparison.Ordinal)).ToList();
        var picked = (errors.Count > 0 ? errors : lines).Distinct().ToList();
        var summary = string.Join("\n", picked.Take(shown)).Trim();
        return picked.Count > shown
            ? $"{summary}\n... and {picked.Count - shown} more line(s); the log has the full compiler output."
            : summary;
    }

    private static readonly Regex MaterialTextureReference = new(
        @"^\s*""?Texture\w*""?\s+""([^""\[][^""]*\.(?:tga|png|jpg|jpeg|psd|tif|tiff|exr))""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Copies textures the CSWin64 copies of the model's materials reference but CSWin64 lacks,
    /// from the CSDK12 addon, then from CSDK12's citadel and core content. Shaders also fall back
    /// to materials/default textures for parameters a material leaves out, so that folder is
    /// copied whole. Existing files are kept. Returns how many files were copied.
    /// </summary>
    public static int SyncMaterialTextures(string csWinMaterialRoot, string csWinAddonRoot, string csdkAddonRoot)
    {
        var csdkContent = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(csdkAddonRoot)));
        var sources = new List<string> { csdkAddonRoot };
        if (csdkContent != null)
            sources.AddRange([Path.Combine(csdkContent, "citadel"), Path.Combine(csdkContent, "core")]);

        var copied = 0;
        var references = Directory.EnumerateFiles(csWinMaterialRoot, "*.vmat", SearchOption.AllDirectories)
            .SelectMany(material => MaterialTextureReference.Matches(File.ReadAllText(material)))
            .Select(match => match.Groups[1].Value.Replace('\\', '/').TrimStart('/'))
            .Where(path => !path.Split('/').Any(part => part is "" or "." or ".."))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var relative = reference.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.Combine(csWinAddonRoot, relative);
            if (File.Exists(target)) continue;
            var source = sources.Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);
            if (source == null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            copied++;
        }

        var defaultsTarget = Path.Combine(csWinAddonRoot, "materials", "default");
        foreach (var root in sources.Skip(1))
        {
            var defaults = Path.Combine(root, "materials", "default");
            if (!Directory.Exists(defaults)) continue;
            foreach (var source in Directory.EnumerateFiles(defaults))
            {
                var target = Path.Combine(defaultsTarget, Path.GetFileName(source));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(defaultsTarget);
                File.Copy(source, target);
                copied++;
            }
        }
        return copied;
    }

    /// <summary>
    /// Copies the compiled resources a model references, directly or through its materials, from
    /// the compiler's game addon to the deployed one. References the compiler did not produce
    /// (stock game files) are skipped. Returns how many files were copied.
    /// </summary>
    public static int DeployCompiledDependencies(string compiledModel, string compilerGameAddon, string deployGameAddon)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(ExternalReferences(compiledModel));
        var copied = 0;
        while (pending.TryDequeue(out var reference))
        {
            var relative = reference.Replace('\\', '/').TrimStart('/') + "_c";
            if (!visited.Add(relative) || relative.Split('/').Any(part => part is "" or "." or "..")) continue;
            var source = Path.Combine(compilerGameAddon, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) continue;
            var target = Path.Combine(deployGameAddon, relative.Replace('/', Path.DirectorySeparatorChar));
            if (NeedsCopy(source, target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                copied++;
            }
            foreach (var next in ExternalReferences(source))
                pending.Enqueue(next);
        }
        return copied;
    }

    private static IEnumerable<string> ExternalReferences(string compiledPath)
    {
        using var resource = new Resource();
        resource.Read(compiledPath);
        return resource.ExternalReferences?.ResourceRefInfoList.Select(info => info.Name).ToList() ?? [];
    }

    public static string? VerifyCompiledAg2References(
        string compiledPath, string? expectedSkelPath, string? expectedGraphPath, string? expectedUiGraphPath,
        IReadOnlyDictionary<string, string>? expectedNamedGraphs = null)
    {
        if (expectedSkelPath == null && expectedGraphPath == null && expectedUiGraphPath == null &&
            expectedNamedGraphs is not { Count: > 0 })
            return null;
        if ((expectedSkelPath != null && string.IsNullOrWhiteSpace(expectedSkelPath)) ||
            (expectedGraphPath != null && string.IsNullOrWhiteSpace(expectedGraphPath)) ||
            (expectedUiGraphPath != null && string.IsNullOrWhiteSpace(expectedUiGraphPath)))
            return "Cannot verify AG2 references because a selected hero preset path is empty.";

        try
        {
            using var resource = new Resource();
            resource.Read(compiledPath);
            var data = (resource.DataBlock?.ToString() ?? string.Empty)
                .Replace("\\u002B", "+", StringComparison.OrdinalIgnoreCase)
                .Replace('\\', '/');
            var missing = new List<string>();

            if (expectedSkelPath != null &&
                (!data.Contains("m_vecNmSkeletonRefs", StringComparison.OrdinalIgnoreCase) ||
                 !data.Contains(expectedSkelPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                missing.Add($"NmSkeletonReference ({expectedSkelPath})");

            if (expectedGraphPath != null &&
                (!data.Contains("m_animGraph2Refs", StringComparison.OrdinalIgnoreCase) ||
                 !data.Contains(expectedGraphPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                missing.Add($"DefaultAnimGraph2 ({expectedGraphPath})");

            if (expectedUiGraphPath != null)
            {
                var uiFound = Regex.Matches(data, @"\{[\s\S]*?\}")
                    .Any(item => Regex.IsMatch(item.Value, @"\bm_sIdentifier\s*=\s*""ui""", RegexOptions.IgnoreCase) &&
                                 item.Value.Contains(expectedUiGraphPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (!uiFound) missing.Add($"ui AnimGraph2 ({expectedUiGraphPath})");
            }
            if (expectedNamedGraphs != null)
                foreach (var (name, graph) in expectedNamedGraphs)
                {
                    var found = Regex.Matches(data, @"\{[\s\S]*?\}")
                        .Any(item => Regex.IsMatch(item.Value, @"\bm_sIdentifier\s*=\s*""" + Regex.Escape(name) + @"""", RegexOptions.IgnoreCase) &&
                                     item.Value.Contains(graph.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                    if (!found) missing.Add($"{name} AnimGraph2 ({graph})");
                }

            return missing.Count == 0 ? null :
                $"Compiled model is missing AG2 references: {string.Join(", ", missing)}. The model was not deployed.";
        }
        catch (Exception ex)
        {
            return $"Could not verify AG2 references in compiled model: {ex.Message}. The model was not deployed.";
        }
    }

    internal static bool NeedsCopy(string source, string destination)
    {
        var from = new FileInfo(source);
        var to = new FileInfo(destination);
        // File.Copy keeps the timestamp, so any difference means the source was replaced.
        return !to.Exists || from.Length != to.Length || from.LastWriteTimeUtc != to.LastWriteTimeUtc;
    }

    public static string BuildResourceCompilerArguments(string vmdlPath, string gamePath,
        bool skipMaterialCompilation)
    {
        var skipTypes = skipMaterialCompilation ? " -skiptype vmat,vtex" : string.Empty;
        return $"-f{skipTypes} -i \"{vmdlPath}\" -game \"{gamePath}\"";
    }

    public static async Task<OperationResult> ProcessVmdlFileAsync(
        string filepath,
        string? skelPath = null,
        string? graphPath = null,
        string? uiGraphPath = null,
        bool createBackup = true,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        bool upgradeHeader = true,
        bool compileCsWin = true,
        bool revertVmdl = true,
        string? cswinDir = null,
        string? citadelAddonsDir = null,
        bool disableAnimationList = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null,
        Action<string>? beforeDeploy = null,
        Action<string, string>? afterDeploy = null,
        bool autoDetectAnims = true,
        IReadOnlyDictionary<string, string>? namedGraphs = null,
        bool skipMaterialCompilation = false,
        CancellationToken cancellationToken = default,
        List<string>? compilerErrors = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return OperationResult.Fail($"File not found: {filepath}");

        progress?.Report(new CompileProgress(10, "[1/5] preparing source", Path.GetFileName(filepath)));

        var (defSkel, defGraph, defUiGraph) = CsdkPaths.DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath :
            (namedGraphs is { Count: > 0 } ? string.Empty : defGraph);
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;
        var useNamedGraphs = addGraph ? namedGraphs : null;

        var origContent = await File.ReadAllTextAsync(filepath);

        var upgrade = UpgradeVmdlContent(
            origContent,
            skelPath: useSkel,
            graphPath: useGraph,
            uiGraphPath: useUiGraph,
            addSkel: addSkel,
            addGraph: addGraph,
            addUiGraph: addUiGraph,
            upgradeHeader: upgradeHeader,
            namedGraphs: useNamedGraphs
        );

        if (upgrade.Error != null)
            return OperationResult.Fail(upgrade.Error);
        var (upgradedContent, changes) = (upgrade.Content, upgrade.Changes);

        if (changes.Count > 0)
        {
            onLog?.Invoke($"[ag2] upgraded syntax: {string.Join(", ", changes)}");
        }

        var stepLogs = new List<string>();

        if (compileCsWin)
        {
            var (compSuccess, compMsg) = await CompileViaCsWinAndDeployAsync(
                filepath,
                upgradedContent,
                cswinDir: cswinDir,
                citadelAddonsDir: citadelAddonsDir,
                disableAnimationList: disableAnimationList,
                autoDetectAnims: autoDetectAnims,
                skipMaterialCompilation: skipMaterialCompilation,
                progress: progress,
                onLog: onLog,
                expectedSkelPath: addSkel ? useSkel : null,
                expectedGraphPath: addGraph && !(string.IsNullOrWhiteSpace(useGraph) && useNamedGraphs is { Count: > 0 }) ? useGraph : null,
                expectedUiGraphPath: addUiGraph ? useUiGraph : null,
                expectedNamedGraphs: useNamedGraphs,
                beforeDeploy: beforeDeploy,
                afterDeploy: afterDeploy,
                cancellationToken: cancellationToken,
                compilerErrors: compilerErrors
            );

            if (!compSuccess)
                return OperationResult.Fail($"CSWin64 Compilation Failed: {compMsg}");

            stepLogs.Add(compMsg);
        }

        progress?.Report(new CompileProgress(95, "[5/5] finalizing", revertVmdl ? "leaving source unchanged" : "saving vmdl"));

        if (revertVmdl)
        {
            stepLogs.Add("Left CSDK12 VMDL unchanged (ModelDoc compatible)");
        }
        else
        {
            // The source only needs a backup when it is about to be rewritten.
            if (createBackup && upgradedContent != origContent)
            {
                var bakFile = ModelDocTools.CreateUniqueBackup(filepath);
                onLog?.Invoke($"[backup] created backup: {Path.GetFileName(bakFile)}");
            }
            await File.WriteAllTextAsync(filepath, upgradedContent);
            stepLogs.Add($"Saved upgraded VMDL ({string.Join(", ", changes)})");
            onLog?.Invoke($"[save] saved upgraded .vmdl with ag2 node injections");
        }

        progress?.Report(new CompileProgress(100, "[5/5] complete", "model compiled and deployed successfully"));

        return OperationResult.Ok(string.Join(" | ", stepLogs));
    }

    public static async Task<(bool Success, string Message, int FilesCopied)> ExportToCsWinAddonAsync(
        string filepath,
        string? skelPath = null,
        string? graphPath = null,
        string? uiGraphPath = null,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        string? cswinDir = null,
        string? citadelAddonsDir = null,
        IReadOnlyDictionary<string, string>? namedGraphs = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}", 0);

        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : cfg.CsWinDir;
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        var layout = ResolveCsWinLayout(useCsWinDir);
        if (layout == null)
            return (false, $"CSWin64 resourcecompiler.exe was not found in: {useCsWinDir}", 0);

        var (container, addonName, subpath) = CsdkPaths.ParseCsdkPath(filepath, useCitadelDir);

        var (defSkel, defGraph, defUiGraph) = CsdkPaths.DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath :
            (namedGraphs is { Count: > 0 } ? string.Empty : defGraph);
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;

        var origContent = await File.ReadAllTextAsync(filepath);

        var upgrade = UpgradeVmdlContent(
            origContent,
            skelPath: useSkel,
            graphPath: useGraph,
            uiGraphPath: useUiGraph,
            addSkel: addSkel,
            addGraph: addGraph,
            addUiGraph: addUiGraph,
            upgradeHeader: true,
            namedGraphs: namedGraphs
        );

        if (upgrade.Error != null)
            return (false, upgrade.Error, 0);
        var (upgradedContent, changes) = (upgrade.Content, upgrade.Changes);

        int filesCopied = 0;
        var srcModelDir = Path.GetDirectoryName(filepath) ?? string.Empty;
        var contentAddonDir = Path.Combine(layout.ContentRoot, "csgo_addons", addonName);
        var gameAddonDir = Path.Combine(layout.GameRoot, "csgo_addons", addonName);
        var destModelDir = Path.Combine(contentAddonDir, Path.GetDirectoryName(subpath) ?? string.Empty);

        Directory.CreateDirectory(contentAddonDir);
        Directory.CreateDirectory(gameAddonDir);
        Directory.CreateDirectory(destModelDir);

        // Auto-register addon in CSWin64 Workshop Tools via ServerConfig.vdf
        var serverConfigPath = Path.Combine(gameAddonDir, "ServerConfig.vdf");
        if (!File.Exists(serverConfigPath))
        {
            var serverConfigContent = "\"ServerConfig\"\n{\n\t\"bot_quota\"\t\t\"10\"\n\t\"bot_difficulty\"\t\t\"2\"\n\t\"bot_chatter\"\t\t\"normal\"\n\t\"bot_join_team\"\t\t\"any\"\n\t\"bot_defer_to_human_items\"\t\t\"true\"\n\t\"bot_defer_to_human_goals\"\t\t\"true\"\n\t\"bot_join_after_player\"\t\t\"true\"\n\t\"bot_allow_rogues\"\t\t\"true\"\n\t\"bot_allow_pistols\"\t\t\"true\"\n\t\"bot_allow_shotguns\"\t\t\"true\"\n\t\"bot_allow_sub_machine_guns\"\t\t\"true\"\n\t\"bot_allow_machine_guns\"\t\t\"true\"\n\t\"bot_allow_rifles\"\t\t\"true\"\n\t\"bot_allow_snipers\"\t\t\"true\"\n\t\"bot_allow_grenades\"\t\t\"true\"\n\t\"bot_controllable\"\t\t\"true\"\n}\n";
            await File.WriteAllTextAsync(serverConfigPath, serverConfigContent);
        }

        // Copy ONLY .vmdl and 3D mesh files (.dmx, .smd, .fbx, .obj) - no materials or textures
        var meshExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dmx", ".smd", ".fbx", ".obj" };

        if (Directory.Exists(srcModelDir))
        {
            var allFiles = Directory.GetFiles(srcModelDir, "*.*", SearchOption.AllDirectories)
                .Where(f => meshExts.Contains(Path.GetExtension(f)) ||
                            string.Equals(f, filepath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var srcFile in allFiles)
            {
                var relFile = Path.GetRelativePath(srcModelDir, srcFile);
                var destFile = Path.Combine(destModelDir, relFile);

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

                // If it is the main .vmdl, write the upgraded version with AG2 nodes
                if (string.Equals(srcFile, filepath, StringComparison.OrdinalIgnoreCase))
                {
                    await File.WriteAllTextAsync(destFile, upgradedContent);
                }
                else
                {
                    File.Copy(srcFile, destFile, overwrite: true);
                }
                filesCopied++;
            }
        }
        else
        {
            var destVmdl = Path.Combine(contentAddonDir, subpath);
            Directory.CreateDirectory(Path.GetDirectoryName(destVmdl)!);
            await File.WriteAllTextAsync(destVmdl, upgradedContent);
            filesCopied++;
        }

        var destVmdlPath = Path.Combine(contentAddonDir, subpath);
        return (true, $"Exported model & {filesCopied} asset(s) to CSWin64 addon: {destVmdlPath}", filesCopied);
    }
}
