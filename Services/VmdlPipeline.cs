using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Services;

public static class VmdlPipeline
{
    public const string ModelDoc41Header = "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:modeldoc41:version{12fc9d44-453a-4ae4-b4d9-7e2ac0bbd4e0} -->";
    public const string DefaultCsWinDir = @"A:\modding\CSWin64";

    public static bool IsValidCsWinDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        var rc1 = Path.Combine(path, "game", "bin", "win64", "resourcecompiler.exe");
        var rc2 = Path.Combine(path, "bin", "win64", "resourcecompiler.exe");
        return File.Exists(rc1) || File.Exists(rc2);
    }

    public static string? ExtractCitadelAddonsDir(string filepath)
    {
        if (string.IsNullOrWhiteSpace(filepath))
            return null;

        var clean = filepath.Replace('\\', '/');
        var m = Regex.Match(clean, @"^(.*?/content/(citadel_addons|citadel_community_addons|citadel))(/|$)", RegexOptions.IgnoreCase);
        if (m.Success)
            return Path.GetFullPath(m.Groups[1].Value);

        var m2 = Regex.Match(clean, @"^(.*?/citadel_addons)(/|$)", RegexOptions.IgnoreCase);
        if (m2.Success)
            return Path.GetFullPath(m2.Groups[1].Value);

        return null;
    }

    public static string? DetectHeroFromPath(string filepath)
    {
        var db = HeroDatabase.GetDatabase();
        var clean = filepath.Replace('\\', '/').ToLowerInvariant();
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        // Check parent folder names from closest upwards
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            var folder = parts[i];
            if (db.ContainsKey(folder))
                return folder;
        }

        // Check filename stem
        var filename = Path.GetFileNameWithoutExtension(filepath).ToLowerInvariant();
        if (db.ContainsKey(filename))
            return filename;

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

    public static string ResolveGameAddonDir(string targetVmdlPath, string? citadelAddonsDir, string addonName)
    {
        // 1. Try resolving relative to targetVmdlPath containing /content/
        var normTarget = targetVmdlPath.Replace('\\', '/');
        int contentIdx = normTarget.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        if (contentIdx >= 0)
        {
            var root = normTarget[..contentIdx];
            var afterContent = normTarget[(contentIdx + "/content/".Length)..];
            var parts = afterContent.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var cand1 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", parts[0], parts[1]);
                if (Directory.Exists(cand1)) return cand1;
                var cand2 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", parts[1]);
                if (Directory.Exists(cand2)) return cand2;
                return cand1;
            }
        }

        // 2. Try resolving relative to citadelAddonsDir
        if (!string.IsNullOrWhiteSpace(citadelAddonsDir))
        {
            var normCitadel = citadelAddonsDir.Replace('\\', '/');
            int citContentIdx = normCitadel.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            if (citContentIdx >= 0)
            {
                var root = normCitadel[..citContentIdx];
                var afterContent = normCitadel[(citContentIdx + "/content/".Length)..].Trim('/');
                var cand = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", afterContent.Replace('/', Path.DirectorySeparatorChar), addonName);
                if (Directory.Exists(cand)) return cand;
                var cand2 = Path.Combine(root.Replace('/', Path.DirectorySeparatorChar), "game", "citadel_addons", addonName);
                if (Directory.Exists(cand2)) return cand2;
                return cand;
            }

            var parent = Directory.GetParent(citadelAddonsDir)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var cand = Path.Combine(parent, "game", "citadel_addons", addonName);
                return cand;
            }
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

        var clean = vmdlPath.Replace('\\', '/');
        var stem = Path.GetFileNameWithoutExtension(clean).ToLowerInvariant();

        var skel = Regex.Replace(clean, @"\.vmdl$", ".vnmskel", RegexOptions.IgnoreCase);
        var graph = $"animgraphs/animgraph2/hero/hero.vnmgraph+{stem}.vnmgraph";
        var uiGraph = $"animgraphs/animgraph2/hero/hero_ui.vnmgraph+{stem}.vnmgraph";

        return (skel, graph, uiGraph);
    }

    public static (string UpgradedContent, List<string> Changes) UpgradeVmdlContent(
        string content,
        string skelPath,
        string graphPath,
        string? uiGraphPath = null,
        bool addSkel = true,
        bool addGraph = true,
        bool addUiGraph = true,
        bool upgradeHeader = true)
    {
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        var changes = new List<string>();

        if (upgradeHeader && lines.Count > 0)
        {
            if (lines[0].Contains("format:modeldoc40") || lines[0].Contains("modeldoc"))
            {
                if (lines[0].Trim() != ModelDoc41Header)
                {
                    lines[0] = ModelDoc41Header;
                    changes.Add("Upgraded header format to modeldoc41");
                }
            }
        }

        var fullText = string.Join("\n", lines);
        var hasNmSkel = fullText.Contains("NmSkeletonList");
        var hasAnimGraph = fullText.Contains("AnimGraph2List") || fullText.Contains("DefaultAnimGraph2");

        var nodesToInject = new List<(string Name, string Text)>();

        if (addSkel && !hasNmSkel)
        {
            var skelBlock = $"\t\t\t{{\n\t\t\t\t_class = \"NmSkeletonList\"\n\t\t\t\tchildren = \n\t\t\t\t[\n\t\t\t\t\t{{\n\t\t\t\t\t\t_class = \"NmSkeletonReference\"\n\t\t\t\t\t\tfilename = \"{skelPath}\"\n\t\t\t\t\t}},\n\t\t\t\t]\n\t\t\t}},\n";
            nodesToInject.Add(("NmSkeletonList", skelBlock));
        }

        if (addGraph && !hasAnimGraph)
        {
            var animChildren = $"\t\t\t\t\t{{\n\t\t\t\t\t\t_class = \"DefaultAnimGraph2\"\n\t\t\t\t\t\tfilename = \"{graphPath}\"\n\t\t\t\t\t}},\n";
            if (addUiGraph && !string.IsNullOrEmpty(uiGraphPath))
            {
                animChildren += $"\t\t\t\t\t{{\n\t\t\t\t\t\t_class = \"AnimGraph2\"\n\t\t\t\t\t\tname = \"ui\"\n\t\t\t\t\t\tfilename = \"{uiGraphPath}\"\n\t\t\t\t\t}},\n";
            }
            var graphBlock = $"\t\t\t{{\n\t\t\t\t_class = \"AnimGraph2List\"\n\t\t\t\tchildren = \n\t\t\t\t[\n{animChildren}\t\t\t\t]\n\t\t\t}},\n";
            nodesToInject.Add(("AnimGraph2List", graphBlock));
        }

        if (nodesToInject.Count == 0)
        {
            return (string.Join("\n", lines), changes);
        }

        int insertIdx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains("model_archetype") || lines[i].Contains("primary_associated_entity"))
            {
                if (i > 0 && lines[i - 1].Trim() == "]")
                {
                    insertIdx = i - 1;
                }
                else
                {
                    insertIdx = i;
                }
                break;
            }
        }

        if (insertIdx != -1)
        {
            foreach (var (name, text) in nodesToInject)
            {
                lines.Insert(insertIdx++, text.TrimEnd('\r', '\n'));
                changes.Add($"Injected {name} node");
            }
        }
        else
        {
            changes.Add("Error: Could not locate rootNode children closing bracket");
        }

        return (string.Join("\n", lines), changes);
    }

    public record CompileProgress(
        int Step,
        int TotalSteps,
        int Percent,
        string Stage,
        string Detail
    );

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
        bool autoDetectAnims = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null)
    {
        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : (!string.IsNullOrWhiteSpace(cfg.CsWinDir) ? cfg.CsWinDir : DefaultCsWinDir);
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        var rcExe = Path.Combine(useCsWinDir, "game", "bin", "win64", "resourcecompiler.exe");
        var csWinGameDir = Path.Combine(useCsWinDir, "game", "csgo");

        if (!File.Exists(rcExe))
        {
            var altRcExe = Path.Combine(useCsWinDir, "bin", "win64", "resourcecompiler.exe");
            if (File.Exists(altRcExe))
            {
                rcExe = altRcExe;
            }
            else
            {
                return (false, $"CSWin64 resourcecompiler.exe not found in: {useCsWinDir}");
            }
        }

        var (container, addonName, subpath) = ParseCsdkPath(csdk12VmdlPath, useCitadelDir);

        var csWinVmdlPath = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
        var csWinVmdlDir = Path.GetDirectoryName(csWinVmdlPath)!;
        var csdkVmdlDir = Path.GetDirectoryName(csdk12VmdlPath) ?? string.Empty;
        Directory.CreateDirectory(csWinVmdlDir);

        // 1. Sync mesh/model files (.dmx, .fbx, .smd, .obj, .vmat, .png, .vanim) to CSWin64 so resourcecompiler finds them
        if (!string.IsNullOrEmpty(csdkVmdlDir) && Directory.Exists(csdkVmdlDir))
        {
            progress?.Report(new CompileProgress(2, 5, 25, "[2/5] syncing assets", "scanning model assets..."));
            onLog?.Invoke("[sync] scanning for model assets (.dmx, .fbx, .smd, .vmat, .png, .vanim)...");

            var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".dmx", ".fbx", ".smd", ".obj", ".vmat", ".png", ".vanim"
            };

            var filesToCopy = Directory.EnumerateFiles(csdkVmdlDir, "*.*", SearchOption.AllDirectories)
                .Where(f => allowedExts.Contains(Path.GetExtension(f)))
                .ToList();

            int copied = 0;
            foreach (var srcFile in filesToCopy)
            {
                var relFile = Path.GetRelativePath(csdkVmdlDir, srcFile);
                var dstFile = Path.Combine(csWinVmdlDir, relFile);
                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                if (!File.Exists(dstFile) || File.GetLastWriteTimeUtc(srcFile) > File.GetLastWriteTimeUtc(dstFile))
                {
                    try
                    {
                        File.Copy(srcFile, dstFile, overwrite: true);
                        copied++;
                        progress?.Report(new CompileProgress(
                            2,
                            5,
                            25 + (int)(20.0 * copied / Math.Max(1, filesToCopy.Count)),
                            "[2/5] syncing assets",
                            relFile
                        ));
                        onLog?.Invoke($"[sync] copied: {relFile}");
                    }
                    catch { }
                }
            }
            onLog?.Invoke($"[sync] synchronized {copied} updated asset(s) to cswin64");
        }

        // 2. Smart-disable: auto-detect animation files, disable only missing ones; or fall back to manual flag
        string csWinContent;
        if (autoDetectAnims)
        {
            string csdkAddonRoot = string.Empty;
            var normCsdk = csdk12VmdlPath.Replace('\\', '/');
            var addonMarker = $"/content/{container}/{addonName}/";
            var markerIdx = normCsdk.IndexOf(addonMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                csdkAddonRoot = normCsdk[..(markerIdx + addonMarker.Length - 1)].Replace('/', Path.DirectorySeparatorChar);
            }
            else if (!string.IsNullOrWhiteSpace(useCitadelDir))
            {
                csdkAddonRoot = Path.Combine(useCitadelDir, addonName);
            }

            var csWinAddonRoot = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName);

            var (detectedContent, foundCount, missingCount) = AutoDisableAnimationNodesForCompilation(
                upgradedVmdlContent,
                csdkVmdlDir,
                csWinVmdlDir,
                csdkAddonRoot,
                csWinAddonRoot
            );
            csWinContent = detectedContent;
            if (foundCount > 0)
            {
                onLog?.Invoke($"[anims] auto-detected {foundCount} animation file(s) (missing: {missingCount}) - keeping animationlist enabled");
            }
            else if (missingCount > 0)
            {
                onLog?.Invoke($"[anims] no animation files found on disk ({missingCount} missing) - safely disabled animationlist");
            }
        }
        else
        {
            csWinContent = DisableAnimationNodesForCompilation(upgradedVmdlContent, disableAnimationList);
            if (disableAnimationList)
            {
                onLog?.Invoke("[anims] animationlist disabled via option");
            }
        }

        await File.WriteAllTextAsync(csWinVmdlPath, csWinContent);
        onLog?.Invoke("[prepare] wrote temporary modeldoc definition to cswin64 addon");

        var psi = new ProcessStartInfo
        {
            FileName = rcExe,
            Arguments = $"-f -i \"{csWinVmdlPath}\" -game \"{csWinGameDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        progress?.Report(new CompileProgress(4, 5, 60, "[4/5] compiling model", "resourcecompiler.exe"));
        onLog?.Invoke($"[compiler] starting: resourcecompiler.exe -f -i \"{Path.GetFileName(csWinVmdlPath)}\"");

        var outputLines = new List<string>();
        var errorLines = new List<string>();
        var rawLines = new List<string>();

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                rawLines.Add(e.Data);
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    outputLines.Add(cleaned!);
                    progress?.Report(new CompileProgress(4, 5, 75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64] {cleaned!}");
                }
            }
        };
        proc.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                rawLines.Add(e.Data);
                if (!IsCompilerNoiseLine(e.Data, out var cleaned))
                {
                    errorLines.Add(cleaned!);
                    progress?.Report(new CompileProgress(4, 5, 75, "[4/5] compiling model", cleaned!));
                    onLog?.Invoke($"[cswin64 err] {cleaned!}");
                }
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var msg = errorLines.Count > 0 
                ? string.Join("\n", errorLines) 
                : (outputLines.Count > 0 ? string.Join("\n", outputLines) : string.Join("\n", rawLines));
            return (false, $"CSWin64 Compiler error (code {proc.ExitCode}): {msg.Trim()}");
        }

        var csWinCompiledVmdlc = Path.Combine(useCsWinDir, "game", "csgo_addons", addonName, subpath + "_c");
        if (!File.Exists(csWinCompiledVmdlc))
        {
            return (false, $"Compiler finished but .vmdl_c was not created at: {csWinCompiledVmdlc}");
        }

        progress?.Report(new CompileProgress(5, 5, 90, "[5/5] deploying model", Path.GetFileName(csWinCompiledVmdlc)));

        string csdk12GameVmdlc;
        var cleanPath = csdk12VmdlPath.Replace('\\', '/');

        if (cleanPath.Contains("/content/", StringComparison.OrdinalIgnoreCase))
        {
            var idx = cleanPath.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            var root = cleanPath[..idx];
            csdk12GameVmdlc = Path.Combine(root, "game", container, addonName, subpath + "_c");
        }
        else if (!string.IsNullOrWhiteSpace(useCitadelDir) && useCitadelDir.Replace('\\', '/').Contains("/content/", StringComparison.OrdinalIgnoreCase))
        {
            var cleanCitadel = useCitadelDir.Replace('\\', '/');
            var idx = cleanCitadel.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
            var root = cleanCitadel[..idx];
            csdk12GameVmdlc = Path.Combine(root, "game", container, addonName, subpath + "_c");
        }
        else
        {
            csdk12GameVmdlc = Path.ChangeExtension(csdk12VmdlPath, ".vmdl_c");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(csdk12GameVmdlc)!);
        File.Copy(csWinCompiledVmdlc, csdk12GameVmdlc, overwrite: true);

        var vmdlcSize = new FileInfo(csdk12GameVmdlc).Length;
        onLog?.Invoke($"[deploy] deployed .vmdl_c ({vmdlcSize / 1024:N0} KB) to: {csdk12GameVmdlc}");

        return (true, $"Compiled via CSWin64 & deployed .vmdl_c to: {csdk12GameVmdlc}");
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
        bool autoDetectAnims = true,
        IProgress<CompileProgress>? progress = null,
        Action<string>? onLog = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}");

        progress?.Report(new CompileProgress(1, 5, 10, "[1/5] preparing source", Path.GetFileName(filepath)));

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath : defGraph;
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
            upgradeHeader: upgradeHeader
        );

        if (changes.Count > 0)
        {
            onLog?.Invoke($"[ag2] upgraded syntax: {string.Join(", ", changes)}");
        }

        if (createBackup)
        {
            var bakFile = filepath + ".bak";
            File.Copy(filepath, bakFile, overwrite: true);
            onLog?.Invoke($"[backup] created backup: {Path.GetFileName(bakFile)}");
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
                onLog: onLog
            );

            if (!compSuccess)
                return (false, $"CSWin64 Compilation Failed: {compMsg}");

            stepLogs.Add(compMsg);
        }

        progress?.Report(new CompileProgress(5, 5, 95, "[5/5] finalizing", revertVmdl ? "reverting vmdl" : "saving vmdl"));

        if (revertVmdl)
        {
            await File.WriteAllTextAsync(filepath, origContent);
            stepLogs.Add("Reverted CSDK12 VMDL to pre-upgrade format (ModelDoc compatible)");
            onLog?.Invoke("[revert] reverted working .vmdl file back to original clean format");
        }
        else
        {
            await File.WriteAllTextAsync(filepath, upgradedContent);
            stepLogs.Add($"Saved upgraded VMDL ({string.Join(", ", changes)})");
            onLog?.Invoke($"[save] saved upgraded .vmdl with ag2 node injections");
        }

        progress?.Report(new CompileProgress(5, 5, 100, "[5/5] complete", "model compiled and deployed successfully"));
        onLog?.Invoke($"[success] compilation finished successfully for {Path.GetFileName(filepath)}!");

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
        string? citadelAddonsDir = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}", 0);

        var cfg = ConfigManager.LoadConfig();
        var useCsWinDir = !string.IsNullOrWhiteSpace(cswinDir) ? cswinDir : (!string.IsNullOrWhiteSpace(cfg.CsWinDir) ? cfg.CsWinDir : DefaultCsWinDir);
        var useCitadelDir = !string.IsNullOrWhiteSpace(citadelAddonsDir) ? citadelAddonsDir : cfg.CitadelAddonsDir;

        if (!Directory.Exists(useCsWinDir))
            return (false, $"CSWin64 directory does not exist: {useCsWinDir}", 0);

        var (container, addonName, subpath) = ParseCsdkPath(filepath, useCitadelDir);

        var (defSkel, defGraph, defUiGraph) = DeriveDefaultPaths(filepath);
        var useSkel = !string.IsNullOrWhiteSpace(skelPath) ? skelPath : defSkel;
        var useGraph = !string.IsNullOrWhiteSpace(graphPath) ? graphPath : defGraph;
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
            upgradeHeader: true
        );

        int filesCopied = 0;
        var srcModelDir = Path.GetDirectoryName(filepath) ?? string.Empty;
        var contentAddonDir = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName);
        var gameAddonDir = Path.Combine(useCsWinDir, "game", "csgo_addons", addonName);
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
            var destVmdl = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
            Directory.CreateDirectory(Path.GetDirectoryName(destVmdl)!);
            await File.WriteAllTextAsync(destVmdl, upgradedContent);
            filesCopied++;
        }

        var destVmdlPath = Path.Combine(useCsWinDir, "content", "csgo_addons", addonName, subpath);
        return (true, $"Exported model & {filesCopied} asset(s) to CSWin64 addon: {destVmdlPath}", filesCopied);
    }

    public static string DisableAnimationNodesForCompilation(string content, bool disableAnimationList = true)
    {
        if (disableAnimationList)
        {
            content = DisableNodeByClass(content, "AnimationList");
        }
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");
        return content;
    }

    /// <summary>
    /// Auto-detects animation source files on disk and selectively disables only
    /// AnimFile nodes whose source_filename cannot be resolved. If no animation
    /// files exist at all the whole AnimationList is disabled. Always disables
    /// EmptyAnimGraph and AnimGraph nodes.
    /// </summary>
    public static (string Content, int FoundCount, int MissingCount) AutoDisableAnimationNodesForCompilation(
        string content,
        string csdkVmdlDir,
        string csWinVmdlDir,
        string csdkAddonRoot,
        string csWinAddonRoot)
    {
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");

        if (!content.Contains("AnimationList"))
            return (content, 0, 0);

        var animFilePattern = new Regex(@"_class\s*=\s*""AnimFile""", RegexOptions.IgnoreCase);
        var sourcePattern = new Regex(@"source_filename\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
        var disabledLinePattern = new Regex(@"^[ \t]*disabled\s*=\s*(true|false)[ \t]*[\r\n]*", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        int foundCount = 0;
        int missingCount = 0;

        // Work in reverse order so string indices stay valid as we modify content
        var allMatches = animFilePattern.Matches(content).Cast<Match>().Reverse().ToList();

        foreach (var match in allMatches)
        {
            int classIdx = match.Index;

            // Find the opening brace of this AnimFile block
            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{') { openBrace = i; break; }
                if (content[i] == '}') break;
            }
            if (openBrace == -1) continue;

            // Find matching closing brace
            int depth = 0, closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}') { depth--; if (depth == 0) { closeBrace = i; break; } }
            }
            if (closeBrace == -1) continue;

            var block = content.Substring(openBrace, closeBrace - openBrace + 1);
            var sfMatch = sourcePattern.Match(block);
            if (!sfMatch.Success) continue;

            var sourceFilename = sfMatch.Groups[1].Value.Replace('\\', '/');
            var baseName = Path.GetFileName(sourceFilename);
            var relPath = sourceFilename.Replace('/', Path.DirectorySeparatorChar);

            var candidates = new[]
            {
                Path.Combine(csWinAddonRoot, relPath),
                Path.Combine(csWinVmdlDir, relPath),
                Path.Combine(csWinVmdlDir, baseName),
                Path.Combine(csWinVmdlDir, "clips", baseName),
                Path.Combine(csWinVmdlDir, "anims", baseName),
                Path.Combine(csdkAddonRoot, relPath),
                Path.Combine(csdkVmdlDir, relPath),
                Path.Combine(csdkVmdlDir, baseName),
                Path.Combine(csdkVmdlDir, "clips", baseName),
                Path.Combine(csdkVmdlDir, "anims", baseName),
            };

            bool fileExists = candidates.Any(File.Exists);

            // Strip any existing disabled= line from the block first
            var cleanedBlock = disabledLinePattern.Replace(block, string.Empty);

            if (fileExists)
            {
                foundCount++;
                // Ensure no disabled=true is present (file exists, keep it active)
                content = content.Remove(openBrace, closeBrace - openBrace + 1).Insert(openBrace, cleanedBlock);
            }
            else
            {
                missingCount++;
                // Inject disabled = true right after _class = "AnimFile"
                var disabledBlock = Regex.Replace(cleanedBlock,
                    @"(_class\s*=\s*""AnimFile"")",
                    "$1\n\t\t\t\t\t\tdisabled = true",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
                content = content.Remove(openBrace, closeBrace - openBrace + 1).Insert(openBrace, disabledBlock);
            }
        }

        // If no animation files found at all, disable the whole AnimationList to be safe
        if (foundCount == 0 && missingCount > 0)
            content = DisableNodeByClass(content, "AnimationList");

        return (content, foundCount, missingCount);
    }

    private static string DisableNodeByClass(string content, string className)
    {
        var pattern = @"_class\s*=\s*""" + Regex.Escape(className) + @"""";
        int searchStart = 0;

        while (true)
        {
            if (searchStart >= content.Length) break;
            var match = Regex.Match(content[searchStart..], pattern, RegexOptions.IgnoreCase);
            if (!match.Success) break;

            int classIdx = searchStart + match.Index;

            // Find the opening brace of this node block
            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{')
                {
                    openBrace = i;
                    break;
                }
                if (content[i] == '}')
                    break;
            }

            if (openBrace == -1)
            {
                searchStart = classIdx + match.Length;
                continue;
            }

            // Find matching closing brace
            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1)
            {
                searchStart = classIdx + match.Length;
                continue;
            }

            // Extract the block content
            var block = content.Substring(openBrace, closeBrace - openBrace + 1);

            // Strip any existing 'disabled = ...' lines in this block to prevent duplicates
            block = Regex.Replace(block, @"^[ \t]*disabled\s*=\s*(true|false)[ \t]*[\r\n]*", "", RegexOptions.IgnoreCase | RegexOptions.Multiline);

            // Insert a clean 'disabled = true' right after _class = "className"
            block = Regex.Replace(block,
                @"(_class\s*=\s*""" + Regex.Escape(className) + @""")",
                "$1\n\t\t\t\tdisabled = true",
                RegexOptions.IgnoreCase);

            content = content.Remove(openBrace, closeBrace - openBrace + 1).Insert(openBrace, block);
            searchStart = openBrace + block.Length;
        }

        return content;
    }

    public static string RemoveModelDocNode(string content, string className)
    {
        while (true)
        {
            var match = Regex.Match(content, @"_class\s*=\s*""" + Regex.Escape(className) + @"""", RegexOptions.IgnoreCase);
            if (!match.Success) break;

            int classIdx = match.Index;

            int openBrace = -1;
            for (int i = classIdx - 1; i >= 0; i--)
            {
                if (content[i] == '{')
                {
                    openBrace = i;
                    break;
                }
                if (content[i] == '}')
                    break;
            }

            if (openBrace == -1) break;

            int depth = 0;
            int closeBrace = -1;
            for (int i = openBrace; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        break;
                    }
                }
            }

            if (closeBrace == -1) break;

            int endIdx = closeBrace + 1;
            while (endIdx < content.Length && (content[endIdx] == ' ' || content[endIdx] == '\t'))
                endIdx++;
            if (endIdx < content.Length && content[endIdx] == ',')
                endIdx++;
            while (endIdx < content.Length && (content[endIdx] == '\r' || content[endIdx] == '\n'))
                endIdx++;

            int startIdx = openBrace;
            while (startIdx > 0 && (content[startIdx - 1] == ' ' || content[startIdx - 1] == '\t'))
                startIdx--;

            content = content.Remove(startIdx, endIdx - startIdx);
        }

        return content;
    }

    public static async Task<(bool Success, string Message)> SanitizeVmdlForModelDocAsync(
        string vmdlPath,
        bool createBackup = true,
        bool disableAnimationList = true)
    {
        vmdlPath = Path.GetFullPath(vmdlPath);
        if (!File.Exists(vmdlPath))
            return (false, $"File not found: {vmdlPath}");

        var content = await File.ReadAllTextAsync(vmdlPath);

        if (createBackup)
        {
            var bak = vmdlPath + ".bak";
            File.Copy(vmdlPath, bak, overwrite: true);
        }

        var changes = new List<string>();

        // 1. Remove NmSkeletonList block if present
        if (content.Contains("NmSkeletonList"))
        {
            content = RemoveModelDocNode(content, "NmSkeletonList");
            changes.Add("Stripped NmSkeletonList");
        }

        // 2. Remove AnimGraph2List block if present
        if (content.Contains("AnimGraph2List"))
        {
            content = RemoveModelDocNode(content, "AnimGraph2List");
            changes.Add("Stripped AnimGraph2List");
        }

        // 3. Remove standalone DefaultAnimGraph2 or AnimGraph2 if present outside list
        if (content.Contains("DefaultAnimGraph2") || content.Contains("AnimGraph2"))
        {
            content = RemoveModelDocNode(content, "DefaultAnimGraph2");
            content = RemoveModelDocNode(content, "AnimGraph2");
            changes.Add("Stripped standalone AnimGraph2 nodes");
        }

        // 4. Ensure AnimationList is disabled = true (without deleting animations) if requested
        if (disableAnimationList)
        {
            var disabledContent = DisableNodeByClass(content, "AnimationList");
            if (disabledContent != content)
            {
                content = disabledContent;
                changes.Add("Set disabled = true on AnimationList");
            }
        }

        var disabledAnimGraphs = DisableNodeByClass(DisableNodeByClass(content, "EmptyAnimGraph"), "AnimGraph");
        if (disabledAnimGraphs != content)
        {
            content = disabledAnimGraphs;
            changes.Add("Disabled anim graph nodes");
        }

        await File.WriteAllTextAsync(vmdlPath, content);

        var msg = changes.Count > 0
            ? $"ModelDoc Fix Applied: {string.Join(", ", changes)}"
            : "VMDL was already clean and ModelDoc compatible";

        return (true, msg);
    }
}
