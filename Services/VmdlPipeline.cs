using System.Diagnostics;
using System.Text.RegularExpressions;
using DeadlockVmdlCompiler.Models;
using ValveResourceFormat;

namespace DeadlockVmdlCompiler.Services;

public static class VmdlPipeline
{
    public const string ModelDoc41Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
    public sealed record CsWinLayout(string ResourceCompiler, string GameRoot, string ContentRoot);

    /// <summary>Accepts either the CSWin64 root or its game directory.</summary>
    public static CsWinLayout? ResolveCsWinLayout(string? path)
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

    public static string? DetectHeroFromPath(string filepath)
    {
        var db = HeroDatabase.GetDatabase();
        var clean = filepath.Replace('\\', '/').ToLowerInvariant();
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        // Variant models can share a parent folder and skeleton with the base model.
        var filename = Path.GetFileNameWithoutExtension(filepath).ToLowerInvariant();
        if (db.ContainsKey(filename)) return filename;

        // Older game folders and local databases can use names removed from the
        // curated preset menu. Resolve them to the remaining preset first.
        var renamedFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadman_danny"] = "deadpack",
            ["familiar"] = "familiar_wip",
            ["ghost"] = "geist",
            ["gigawatt_prisoner"] = "seven",
            ["hornet"] = "vindicta",
            ["inferno"] = "infernus",
            ["lady_geist"] = "geist",
            ["nano"] = "calico",
            ["nurse_harrow"] = "nurse",
            ["rat_king"] = "ratking",
            ["solomon"] = "chessmaster",
            ["synth"] = "pocket",
            ["tengu"] = "ivy",
            ["violet"] = "artist"
        };

        // Check parent folder names from closest upwards
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var folder = parts[i];
            var renamed = renamedFolders
                .Where(pair => folder.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                               folder.StartsWith(pair.Key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(pair => pair.Key.Length)
                .FirstOrDefault();
            if (renamed.Value != null && db.ContainsKey(renamed.Value))
                return renamed.Value;
            if (db.ContainsKey(folder))
                return folder;
            var versionedMatch = db.Keys
                .Where(key => folder.StartsWith(key + "_", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(key => key.Length)
                .FirstOrDefault();
            if (versionedMatch != null)
                return versionedMatch;
        }

        return null;
    }

    public static (string Container, string AddonName, string Subpath) ParseCsdkPath(string csdkPath, string? citadelAddonsDir = null)
    {
        var clean = csdkPath.Replace('\\', '/');

        // 1. Standard pattern: .../content/(citadel_addons|citadel_community_addons|citadel)/<addon_name>/<subpath>
        var m = Regex.Match(clean, @"content/(citadel_addons|citadel_community_addons|citadel)/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);

        // 2. General content subfolder pattern: .../content/<addon_name>/<subpath>
        var m2 = Regex.Match(clean, @"content/([^/]+)/(.+)$", RegexOptions.IgnoreCase);
        if (m2.Success)
            return ("citadel_addons", m2.Groups[1].Value, m2.Groups[2].Value);

        // 3. Relative to configured citadelAddonsDir
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var cleanAddons = citadelAddonsDir.Replace('\\', '/').TrimEnd('/');
            if (clean.StartsWith(cleanAddons + "/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = clean[(cleanAddons.Length + 1)..];
                var parts = rel.Split('/', 2);
                if (parts.Length == 2)
                    return ("citadel_addons", parts[0], parts[1]);
                return ("citadel_addons", "addon", parts[0]);
            }
        }

        return ("citadel_addons", "addon", Path.GetFileName(clean));
    }

    private static string? FindCsdkRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var clean = path.Replace('\\', '/');
        var standard = Regex.Match(clean, @"^(.*?)/content/(citadel_addons|citadel_community_addons|citadel)(/|$)",
            RegexOptions.IgnoreCase);
        if (standard.Success) return standard.Groups[1].Value;
        var index = clean.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        return index >= 0 ? clean[..index] : null;
    }

    /// <summary>The one place that decides where an addon's compiled files live; deploy and packaging share it.</summary>
    public static string ResolveGameAddonDir(string targetVmdlPath, string? citadelAddonsDir, string addonName)
    {
        var (container, _, _) = ParseCsdkPath(targetVmdlPath, citadelAddonsDir);
        var root = FindCsdkRoot(targetVmdlPath) ?? FindCsdkRoot(citadelAddonsDir);
        if (root != null)
            return Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", container, addonName);

        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var parent = Directory.GetParent(citadelAddonsDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
                return Path.Combine(parent, "game", "citadel_addons", addonName);
        }

        return Path.Combine(Path.GetDirectoryName(targetVmdlPath) ?? string.Empty, "game", addonName);
    }

    public static (string Skel, string Graph, string UiGraph) DeriveDefaultPaths(string vmdlPath)
    {
        var db = HeroDatabase.GetDatabase();
        var hero = DetectHeroFromPath(vmdlPath);

        if (hero != null && db.TryGetValue(hero, out var preset))
        {
            return (preset.Skel, preset.Graph, preset.UiGraph);
        }

        // There is no reliable way to infer compiled Deadlock references from a custom VMDL path.
        return (string.Empty, string.Empty, string.Empty);
    }

    public static (string UpgradedContent, List<string> Changes) UpgradeVmdlContent(
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

    public static async Task<(bool Success, string Message)> CompileViaCsWinAndDeployAsync(
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
        CancellationToken cancellationToken = default,
        ICollection<string>? compilerErrors = null)
    {
        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : cfg.CsWinDir;
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        var layout = ResolveCsWinLayout(useCsWinDir);
        if (layout == null)
            return (false, $"CSWin64 resourcecompiler.exe not found in: {useCsWinDir}");
        var rcExe = layout.ResourceCompiler;
        var csWinGameDir = Path.Combine(layout.GameRoot, "csgo");

        var (_, addonName, subpath) = ParseCsdkPath(csdk12VmdlPath, useCitadelDir);

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

        // 1. Sync mesh/model files (.dmx, .fbx, .smd, .obj, .vmat, .png, .vanim) to CSWin64 so resourcecompiler finds them
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
                    return (false, $"Could not sync {relFile} to CSWin64: {ex.Message}");
                }
                copied++;
                progress?.Report(new CompileProgress(25 + (int)(20.0 * copied / Math.Max(1, filesToCopy.Count)),
                    "[2/5] syncing assets",
                    relFile
                ));
                if (listFiles) onLog?.Invoke($"[sync] copied: {relFile}");
            }
            if (copied > 0) onLog?.Invoke($"[sync] copied {copied} new or changed asset(s) to cswin64");
        }

        // Animations can live outside the model folder. Preserve their addon-relative
        // paths in CSWin64 before deciding whether a clip is missing.
        if (!disableAnimationList)
        {
            try
            {
                SynchronizeAnimationSourceFiles(upgradedVmdlContent,
                    csdkVmdlDir ?? string.Empty, csdkAddonRoot, csWinAddonRoot, onLog);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (false, $"Could not sync animation source: {ex.Message}");
            }
        }

        // 2. Keep the manual override and the contributor's automatic mode.
        progress?.Report(new CompileProgress(50, "[3/5] preparing modeldoc", "temporary definition..."));
        var csWinContent = PrepareAnimationNodesForCompilation(upgradedVmdlContent,
            disableAnimationList, autoDetectAnims, csdkVmdlDir ?? string.Empty, csWinVmdlDir,
            csdkAddonRoot, csWinAddonRoot, onLog);

        await File.WriteAllTextAsync(csWinVmdlPath, csWinContent, cancellationToken);

        // A successful compiler exit must not be mistaken for an old output from a previous run.
        if (File.Exists(csWinCompiledVmdlc))
            File.Delete(csWinCompiledVmdlc);

        var psi = new ProcessStartInfo
        {
            FileName = rcExe,
            Arguments = $"-f -i \"{csWinVmdlPath}\" -game \"{csWinGameDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        progress?.Report(new CompileProgress(60, "[4/5] compiling model", "resourcecompiler.exe"));
        onLog?.Invoke($"[compiler] starting: resourcecompiler.exe -f -i \"{Path.GetFileName(csWinVmdlPath)}\"");

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
            var msg = errorLines.Count > 0 
                ? string.Join("\n", errorLines) 
                : (outputLines.Count > 0 ? string.Join("\n", outputLines) : string.Join("\n", rawLines));
            return (false, $"CSWin64 Compiler error (code {proc.ExitCode}): {msg.Trim()}");
        }

        if (!File.Exists(csWinCompiledVmdlc))
        {
            return (false, $"Compiler finished but .vmdl_c was not created at: {csWinCompiledVmdlc}");
        }

        var verificationError = VerifyCompiledAg2References(csWinCompiledVmdlc,
            expectedSkelPath, expectedGraphPath, expectedUiGraphPath, expectedNamedGraphs);
        if (verificationError != null)
            return (false, verificationError);

        progress?.Report(new CompileProgress(90, "[5/5] deploying model", Path.GetFileName(csWinCompiledVmdlc)));

        var csdk12GameVmdlc = Path.GetFullPath(Path.Combine(
            ResolveGameAddonDir(csdk12VmdlPath, useCitadelDir, addonName), subpath + "_c"));

        Directory.CreateDirectory(Path.GetDirectoryName(csdk12GameVmdlc)!);
        beforeDeploy?.Invoke(csdk12GameVmdlc);
        File.Copy(csWinCompiledVmdlc, csdk12GameVmdlc, overwrite: true);
        afterDeploy?.Invoke(csdk12GameVmdlc, csWinCompiledVmdlc);

        var vmdlcSize = new FileInfo(csdk12GameVmdlc).Length;
        onLog?.Invoke($"[deploy] deployed .vmdl_c ({vmdlcSize / 1024:N0} KB) to: {csdk12GameVmdlc}");

        return (true, $"Compiled via CSWin64 & deployed .vmdl_c to: {csdk12GameVmdlc}");
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

    private static bool NeedsCopy(string source, string destination)
    {
        var from = new FileInfo(source);
        var to = new FileInfo(destination);
        // File.Copy keeps the timestamp, so any difference means the source was replaced.
        return !to.Exists || from.Length != to.Length || from.LastWriteTimeUtc != to.LastWriteTimeUtc;
    }

    private static string CreateUniqueBackup(string sourcePath)
    {
        var basePath = sourcePath + ".bak";
        var content = File.ReadAllBytes(sourcePath);
        for (var number = 0; ; number++)
        {
            var destination = number == 0 ? basePath : basePath + "." + number;
            try
            {
                if (File.Exists(destination))
                {
                    // Preserve every previous backup, but do not pile up identical copies.
                    if (File.ReadAllBytes(destination).AsSpan().SequenceEqual(content)) return destination;
                    continue;
                }
                File.Copy(sourcePath, destination, overwrite: false);
                return destination;
            }
            catch (IOException) when (File.Exists(destination))
            {
            }
        }
    }

    public static async Task<(bool Success, string Message)> ProcessVmdlFileAsync(
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
        CancellationToken cancellationToken = default,
        ICollection<string>? compilerErrors = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}");

        progress?.Report(new CompileProgress(10, "[1/5] preparing source", Path.GetFileName(filepath)));

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath :
            (namedGraphs is { Count: > 0 } ? string.Empty : defGraph);
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;
        var useNamedGraphs = addGraph ? namedGraphs : null;

        var origContent = await File.ReadAllTextAsync(filepath);

        var (upgradedContent, changes) = UpgradeVmdlContent(
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

        var upgradeError = changes.FirstOrDefault(change => change.StartsWith("Error:", StringComparison.Ordinal));
        if (upgradeError != null)
            return (false, upgradeError);

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
                return (false, $"CSWin64 Compilation Failed: {compMsg}");

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
                var bakFile = CreateUniqueBackup(filepath);
                onLog?.Invoke($"[backup] created backup: {Path.GetFileName(bakFile)}");
            }
            await File.WriteAllTextAsync(filepath, upgradedContent);
            stepLogs.Add($"Saved upgraded VMDL ({string.Join(", ", changes)})");
            onLog?.Invoke($"[save] saved upgraded .vmdl with ag2 node injections");
        }

        progress?.Report(new CompileProgress(100, "[5/5] complete", "model compiled and deployed successfully"));

        return (true, string.Join(" | ", stepLogs));
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

        var (container, addonName, subpath) = ParseCsdkPath(filepath, useCitadelDir);

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath :
            (namedGraphs is { Count: > 0 } ? string.Empty : defGraph);
        var useUiGraph = !string.IsNullOrWhiteSpace(uiGraphPath) ? uiGraphPath : defUiGraph;

        var origContent = await File.ReadAllTextAsync(filepath);

        var (upgradedContent, changes) = UpgradeVmdlContent(
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

        var upgradeError = changes.FirstOrDefault(change => change.StartsWith("Error:", StringComparison.Ordinal));
        if (upgradeError != null)
            return (false, upgradeError, 0);

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

    public static string DisableAnimationNodesForCompilation(string content, bool disableAnimationList = true)
    {
        // Decompiled CSDK12 sources may already contain disabled = true. The checkbox
        // must override that source state in both directions for the CSWin64 copy.
        content = ModelDocAg2Editor.SetNodeDisabled(content, "AnimationList", disableAnimationList);
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");
        return content;
    }

    public static string PrepareAnimationNodesForCompilation(string content,
        bool disableAnimationList, bool autoDetectAnims, string csdkVmdlDir,
        string csWinVmdlDir, string csdkAddonRoot, string csWinAddonRoot, Action<string>? onLog = null)
    {
        if (disableAnimationList || !autoDetectAnims)
        {
            onLog?.Invoke($"[animations] AnimationList {(disableAnimationList ? "disabled by manual override" : "enabled; automatic detection off")}");
            return DisableAnimationNodesForCompilation(content, disableAnimationList);
        }
        var result = AutoDisableAnimationNodesForCompilation(content,
            csdkVmdlDir, csWinVmdlDir, csdkAddonRoot, csWinAddonRoot);
        onLog?.Invoke($"[animations] {result.FoundCount} clip source(s) found" +
                      (result.MissingCount > 0 ? $", {result.MissingCount} missing and disabled for this compile" : string.Empty));
        return result.Content;
    }

    public static int SynchronizeAnimationSourceFiles(string content, string csdkVmdlDir,
        string csdkAddonRoot, string csWinAddonRoot, Action<string>? onLog = null)
    {
        var copied = 0;
        foreach (var filename in ModelDocAg2Editor.GetAnimationSourcePaths(content))
        {
            var source = AnimationSourceResolver.Resolve(filename, csdkAddonRoot, csdkVmdlDir);
            if (source == null) continue;
            var destination = Path.GetFullPath(Path.Combine(csWinAddonRoot,
                Path.GetRelativePath(csdkAddonRoot, source)));
            if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(csWinAddonRoot)) +
                    Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Animation path leaves the CSWin64 addon: {filename}");
            if (NeedsCopy(source, destination))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                copied++;
            }
        }
        if (copied > 0) onLog?.Invoke($"[sync] copied {copied} animation source(s) from outside the model folder");
        return copied;
    }

    /// <summary>Preserves available clips and disables missing sources at their exact authored paths.</summary>
    public static (string Content, int FoundCount, int MissingCount) AutoDisableAnimationNodesForCompilation(
        string content, string csdkVmdlDir, string csWinVmdlDir, string csdkAddonRoot, string csWinAddonRoot)
    {
        content = DisableAnimationNodesForCompilation(content, disableAnimationList: false);
        return ModelDocAg2Editor.ApplyAnimationFileAvailability(content, filename =>
            AnimationSourceResolver.Resolve(filename, csdkAddonRoot, csdkVmdlDir) != null ||
            AnimationSourceResolver.Resolve(filename, csWinAddonRoot, csWinVmdlDir) != null);
    }

    private static string DisableNodeByClass(string content, string className) =>
        ModelDocAg2Editor.SetNodeDisabled(content, className, disabled: true);

    public static async Task<(bool Success, string Message, bool Changed)> SanitizeVmdlForModelDocAsync(
        string vmdlPath,
        bool createBackup = true,
        bool disableAnimationList = true)
    {
        vmdlPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(vmdlPath))
            return (false, $"File not found: {vmdlPath}", false);

        var content = await File.ReadAllTextAsync(vmdlPath);
        var (clean, changes) = Ag2Sanitizer.SanitizeVmdlContent(content, disableAnimationList);
        if (clean == content)
            return (true, "VMDL is already clean and ModelDoc compatible; nothing was changed", false);

        if (createBackup)
            CreateUniqueBackup(vmdlPath);
        await File.WriteAllTextAsync(vmdlPath, clean);
        return (true, $"ModelDoc Fix Applied: {string.Join(", ", changes)}", true);
    }
}
