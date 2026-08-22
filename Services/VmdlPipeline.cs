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

    public static async Task<(bool Success, string Message)> CompileViaCsWinAndDeployAsync(
        string csdk12VmdlPath,
        string upgradedVmdlContent,
        string? cswinDir = null,
        string? citadelAddonsDir = null)
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
        var csdkVmdlDir = Path.GetDirectoryName(csdk12VmdlPath);
        Directory.CreateDirectory(csWinVmdlDir);

        // 1. Sync mesh/model files (.dmx, .fbx, .smd, .obj, .vmat, .png) to CSWin64 so resourcecompiler finds them
        if (!string.IsNullOrEmpty(csdkVmdlDir) && Directory.Exists(csdkVmdlDir))
        {
            var filesToCopy = Directory.EnumerateFiles(csdkVmdlDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext == ".dmx" || ext == ".fbx" || ext == ".smd" || ext == ".obj" || ext == ".vmat" || ext == ".png";
                });

            foreach (var srcFile in filesToCopy)
            {
                var dstFile = Path.Combine(csWinVmdlDir, Path.GetFileName(srcFile));
                if (!File.Exists(dstFile) || File.GetLastWriteTimeUtc(srcFile) > File.GetLastWriteTimeUtc(dstFile))
                {
                    try { File.Copy(srcFile, dstFile, overwrite: true); } catch { }
                }
            }
        }

        // 2. Disable animation nodes (disabled = true) so CSWin64 doesn't fail on missing animation DMXs
        var csWinContent = DisableAnimationNodesForCompilation(upgradedVmdlContent);

        await File.WriteAllTextAsync(csWinVmdlPath, csWinContent);

        var psi = new ProcessStartInfo
        {
            FileName = rcExe,
            Arguments = $"-f -i \"{csWinVmdlPath}\" -game \"{csWinGameDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;

        if (proc.ExitCode != 0)
        {
            var msg = !string.IsNullOrWhiteSpace(error) ? error : output;
            return (false, $"CSWin64 Compiler error (code {proc.ExitCode}): {msg.Trim()}");
        }

        var csWinCompiledVmdlc = Path.Combine(useCsWinDir, "game", "csgo_addons", addonName, subpath + "_c");
        if (!File.Exists(csWinCompiledVmdlc))
        {
            return (false, $"Compiler finished but .vmdl_c was not created at: {csWinCompiledVmdlc}");
        }

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
        string? citadelAddonsDir = null)
    {
        filepath = Path.GetFullPath(filepath);
        if (!File.Exists(filepath))
            return (false, $"File not found: {filepath}");

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

        if (createBackup)
        {
            var bakFile = filepath + ".bak";
            File.Copy(filepath, bakFile, overwrite: true);
        }

        var stepLogs = new List<string>();

        if (compileCsWin)
        {
            var (compSuccess, compMsg) = await CompileViaCsWinAndDeployAsync(
                filepath,
                upgradedContent,
                cswinDir: cswinDir,
                citadelAddonsDir: citadelAddonsDir
            );

            if (!compSuccess)
                return (false, $"CSWin64 Compilation Failed: {compMsg}");

            stepLogs.Add(compMsg);
        }

        if (revertVmdl)
        {
            await File.WriteAllTextAsync(filepath, origContent);
            stepLogs.Add("Reverted CSDK12 VMDL to pre-upgrade format (ModelDoc compatible)");
        }
        else
        {
            await File.WriteAllTextAsync(filepath, upgradedContent);
            stepLogs.Add($"Saved upgraded VMDL ({string.Join(", ", changes)})");
        }

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

    public static string DisableAnimationNodesForCompilation(string content)
    {
        content = DisableNodeByClass(content, "AnimationList");
        content = DisableNodeByClass(content, "EmptyAnimGraph");
        content = DisableNodeByClass(content, "AnimGraph");
        return content;
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

    public static async Task<(bool Success, string Message)> SanitizeVmdlForModelDocAsync(string vmdlPath, bool createBackup = true)
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

        // 4. Ensure AnimationList is disabled = true (without deleting animations)
        var disabledContent = DisableAnimationNodesForCompilation(content);
        if (disabledContent != content)
        {
            content = disabledContent;
            changes.Add("Set disabled = true on AnimationList");
        }

        await File.WriteAllTextAsync(vmdlPath, content);

        var msg = changes.Count > 0
            ? $"ModelDoc Fix Applied: {string.Join(", ", changes)}"
            : "VMDL was already clean and ModelDoc compatible";

        return (true, msg);
    }
}
