using System.Diagnostics;
using Microsoft.Win32;
using ValveKeyValue;

namespace DeadlockVmdlCompiler.Services;

public class DeadlockInstallInfo
{
    public string GameRootPath { get; set; } = string.Empty;
    public string DeadlockExePath { get; set; } = string.Empty;
    public string Pak01VpkPath { get; set; } = string.Empty;
    public bool IsValid => !string.IsNullOrEmpty(DeadlockExePath) && File.Exists(DeadlockExePath) &&
                           !string.IsNullOrEmpty(Pak01VpkPath) && File.Exists(Pak01VpkPath);
}

public static class DeadlockLocator
{
    private const string DeadlockAppId = "1422450";

    public static DeadlockInstallInfo DetectDeadlockInstallation(string? hintPath = null)
    {
        // 1. If a hint path was provided, validate it first
        if (!string.IsNullOrWhiteSpace(hintPath))
        {
            var fromHint = ValidateAndExtractInfo(hintPath);
            if (fromHint.IsValid) return fromHint;
        }

        return DetectFromSteamLibraries(GetSteamLibraryFolders());
    }

    public static DeadlockInstallInfo DetectFromSteamLibraries(IEnumerable<string> libraryFolders)
    {
        foreach (var library in libraryFolders)
        {
            if (string.IsNullOrWhiteSpace(library)) continue;
            try
            {
                var steamApps = Path.Combine(library, "steamapps");
                var manifest = ReadKeyValues(Path.Combine(steamApps, $"appmanifest_{DeadlockAppId}.acf"));
                if (manifest == null || !string.Equals(manifest.Name, "AppState", StringComparison.OrdinalIgnoreCase) ||
                    GetString(manifest.Root, "appid") != DeadlockAppId) continue;
                var installDir = GetString(manifest.Root, "installdir");
                if (string.IsNullOrWhiteSpace(installDir) || Path.IsPathRooted(installDir) || installDir.Contains(':')) continue;
                var common = Path.GetFullPath(Path.Combine(steamApps, "common"));
                var candidate = Path.GetFullPath(Path.Combine(common, installDir));
                if (!candidate.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var info = ExtractInfo(candidate);
                if (!info.IsValid) info = ExtractInfo(candidate, flatLayout: true);
                if (info.IsValid) return info;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new DeadlockInstallInfo();
    }

    public static DeadlockInstallInfo ValidateAndExtractInfo(string candidateDir)
    {
        if (string.IsNullOrWhiteSpace(candidateDir)) return new DeadlockInstallInfo();
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDir));
            if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(full)) return new DeadlockInstallInfo();
            var roots = new List<string>();
            for (var dir = full; dir != null && roots.Count < 4; dir = Directory.GetParent(dir)?.FullName)
                roots.Add(dir);
            // Find the installation root even when the hint is game/, bin/win64/ or a VPK file.
            foreach (var root in roots)
            {
                var info = ExtractInfo(root);
                if (info.IsValid) return info;
            }
            foreach (var root in roots)
            {
                var info = ExtractInfo(root, flatLayout: true);
                if (info.IsValid) return info;
            }
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new DeadlockInstallInfo();
    }

    private static DeadlockInstallInfo ExtractInfo(string root, bool flatLayout = false)
    {
        var game = flatLayout ? root : Path.Combine(root, "game");
        var exe = new[] { "deadlock.exe", "project8.exe" }
            .Select(name => Path.Combine(game, "bin", "win64", name)).FirstOrDefault(File.Exists);
        var vpk = new[] { Path.Combine(game, "citadel", "pak01_dir.vpk"), Path.Combine(game, "pak01_dir.vpk") }
            .FirstOrDefault(File.Exists);
        return exe != null && vpk != null
            ? new DeadlockInstallInfo { GameRootPath = root, DeadlockExePath = exe, Pak01VpkPath = vpk }
            : new DeadlockInstallInfo();
    }

    public static List<string> GetSteamLibraryFolders()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path) steamRoots.Add(path);
                if (key?.GetValue("SteamExe") is string exe && Path.GetDirectoryName(exe) is { } dir) steamRoots.Add(dir);
            }
            catch { }

            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = hive.OpenSubKey(@"SOFTWARE\Valve\Steam");
                    if (key?.GetValue("InstallPath") is string path) steamRoots.Add(path);
                }
                catch { }
            }
        }

        // A running client also supports installations without registry entries.
        try
        {
            foreach (var process in Process.GetProcessesByName("steam"))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName is string exe && Path.GetDirectoryName(exe) is { } dir)
                            steamRoots.Add(dir);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return GetSteamLibraryFolders(steamRoots);
    }

    public static List<string> GetSteamLibraryFolders(IEnumerable<string> steamRoots)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in steamRoots)
        {
            var root = ExistingDirectory(steamRoot);
            if (root == null) continue;
            libraries.Add(root);
            foreach (var relative in new[] { "steamapps", "config" })
            {
                var document = ReadKeyValues(Path.Combine(root, relative, "libraryfolders.vdf"));
                if (document == null || !string.Equals(document.Name, "libraryfolders", StringComparison.OrdinalIgnoreCase) ||
                    !document.Root.IsCollection) continue;
                foreach (var (key, entry) in document.Root.Children)
                {
                    if (key.Length == 0 || !key.All(char.IsAsciiDigit)) continue;
                    // Modern Steam uses a path field; older clients store the path directly.
                    var path = entry.IsCollection ? GetString(entry, "path") :
                        entry.ValueType == KVValueType.String ? (string)entry : string.Empty;
                    var library = ExistingDirectory(path);
                    if (library != null) libraries.Add(library);
                }
            }
        }

        return libraries.ToList();
    }

    private static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Directory.Exists(full) ? full : null;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static KVDocument? ReadKeyValues(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return KVSerializer.Create(KVSerializationFormat.KeyValues1Text).Deserialize(stream,
                new KVSerializerOptions { HasEscapeSequences = true });
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (KeyValueException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string GetString(KVObject node, string key)
    {
        if (!node.IsCollection) return string.Empty;
        foreach (var (name, value) in node.Children)
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase) && !value.IsCollection && !value.IsArray && !value.IsNull)
                return value.ToString();
        return string.Empty;
    }
}
