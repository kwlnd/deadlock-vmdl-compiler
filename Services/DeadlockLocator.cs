using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using ValveKeyValue;

namespace DeadlockVmdlCompiler.Services;

public class DeadlockInstallInfo
{
    public string GameRootPath { get; set; } = string.Empty;
    public string ModDirectoryName { get; set; } = string.Empty;
    public string DeadlockExePath { get; set; } = string.Empty;
    public string Pak01VpkPath { get; set; } = string.Empty;
    public bool IsValid => !string.IsNullOrEmpty(DeadlockExePath) && File.Exists(DeadlockExePath) &&
                           !string.IsNullOrEmpty(Pak01VpkPath) && File.Exists(Pak01VpkPath);
}

/// <summary>
/// Finds Deadlock from what Steam and the game record about themselves: the Windows uninstall
/// entry, Steam's library list and app manifest, and the game's own steam.inf. No folder or
/// file names are guessed.
/// </summary>
public static class DeadlockLocator
{
    private const string DeadlockAppId = "1422450";
    private static DeadlockInstallInfo? _cached;

    public static DeadlockInstallInfo DetectDeadlockInstallation(string? hintPath = null)
    {
        if (!string.IsNullOrWhiteSpace(hintPath))
        {
            var fromHint = ValidateAndExtractInfo(hintPath);
            if (fromHint.IsValid) return fromHint;
        }

        if (_cached is { IsValid: true }) return _cached;

        // Steam registers every installed app with Windows, including its real location.
        foreach (var location in GetRegisteredInstallLocations())
        {
            var info = InspectInstallation(location, null);
            if (info.IsValid) return _cached = info;
        }

        var fromLibraries = DetectFromSteamLibraries(GetSteamLibraryFolders());
        if (fromLibraries.IsValid) _cached = fromLibraries;
        return fromLibraries;
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
                var info = InspectInstallation(candidate, GetString(manifest.Root, "name"));
                if (info.IsValid) return info;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new DeadlockInstallInfo();
    }

    /// <summary>Accepts any path inside the installation: its root, game folder, a VPK or the executable.</summary>
    public static DeadlockInstallInfo ValidateAndExtractInfo(string candidateDir)
    {
        if (string.IsNullOrWhiteSpace(candidateDir)) return new DeadlockInstallInfo();
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDir));
            if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
            if (!Directory.Exists(full)) return new DeadlockInstallInfo();
            var levels = 0;
            for (var dir = full; dir != null && levels < 5; dir = Directory.GetParent(dir)?.FullName, levels++)
            {
                var info = InspectInstallation(dir, null);
                if (info.IsValid) return info;
            }
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new DeadlockInstallInfo();
    }

    /// <summary>
    /// The game identifies its own content folder with a steam.inf carrying its app id. The
    /// archive sits beside that file, and the launcher is found beside the content folder.
    /// </summary>
    private static DeadlockInstallInfo InspectInstallation(string root, string? displayName)
    {
        try
        {
            if (!Directory.Exists(root)) return new DeadlockInstallInfo();
            var search = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true
            };
            foreach (var steamInf in Directory.EnumerateFiles(root, "steam.inf", search))
            {
                if (!IsDeadlockSteamInf(steamInf)) continue;
                var modDirectory = Path.GetDirectoryName(steamInf)!;
                var archive = Path.Combine(modDirectory, "pak01_dir.vpk");
                var gameDirectory = Path.GetDirectoryName(modDirectory);
                if (!File.Exists(archive) || gameDirectory == null) continue;

                var modName = Path.GetFileName(modDirectory);
                var launcher = FindLauncher(gameDirectory, modName, displayName ?? Path.GetFileName(root));
                if (launcher == null) continue;
                return new DeadlockInstallInfo
                {
                    GameRootPath = Path.GetDirectoryName(gameDirectory) ?? gameDirectory,
                    ModDirectoryName = modName,
                    DeadlockExePath = launcher,
                    Pak01VpkPath = archive
                };
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new DeadlockInstallInfo();
    }

    private static bool IsDeadlockSteamInf(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator > 0 && line[..separator].Trim().Equals("appID", StringComparison.OrdinalIgnoreCase))
                    return line[(separator + 1)..].Trim() == DeadlockAppId;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>
    /// Picks the game launcher among the executables shipped with the engine. A Source 2 launcher
    /// embeds the name of the content folder it starts; tools such as vconsole do not.
    /// </summary>
    private static string? FindLauncher(string gameDirectory, string modName, string? displayName)
    {
        var binaries = Path.Combine(gameDirectory, "bin");
        if (!Directory.Exists(binaries)) return null;
        var executables = Directory.EnumerateFiles(binaries, "*.exe", new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true
        }).ToList();
        if (executables.Count <= 1) return executables.FirstOrDefault();

        var embedded = Encoding.Unicode.GetBytes(modName);
        var launchers = executables.Where(exe =>
        {
            try { return new FileInfo(exe).Length < 64 * 1024 * 1024 && File.ReadAllBytes(exe).AsSpan().IndexOf(embedded) >= 0; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }).ToList();
        if (launchers.Count == 1) return launchers[0];

        // Steam's own name for the app is the next best evidence.
        var pool = launchers.Count > 0 ? launchers : executables;
        static string Key(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        var named = pool.FirstOrDefault(exe => Key(displayName).Length > 0 &&
                                               Key(Path.GetFileNameWithoutExtension(exe)) == Key(displayName));
        // A launcher is a small stub; engine tools are many times larger.
        return named ?? pool.OrderBy(exe => new FileInfo(exe).Length).First();
    }

    private static IEnumerable<string> GetRegisteredInstallLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + DeadlockAppId;
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default)
                 })
        {
            string? location = null;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(uninstallKey);
                location = key?.GetValue("InstallLocation") as string;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            if (ExistingDirectory(location) is { } directory) yield return directory;
        }
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
        var libraries = new List<string>();
        var listingApp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string library, bool listsApp)
        {
            if (!libraries.Contains(library, StringComparer.OrdinalIgnoreCase)) libraries.Add(library);
            if (listsApp) listingApp.Add(library);
        }

        foreach (var steamRoot in steamRoots)
        {
            var root = ExistingDirectory(steamRoot);
            if (root == null) continue;
            Add(root, false);
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
                    if (library == null) continue;
                    var listsApp = entry.IsCollection && entry.Children.Any(child =>
                        child.Key.Equals("apps", StringComparison.OrdinalIgnoreCase) && child.Value.IsCollection &&
                        child.Value.Children.Any(app => app.Key == DeadlockAppId));
                    Add(library, listsApp);
                }
            }
        }

        // Steam says which library holds the app; look there first, then verify the rest.
        return libraries.OrderByDescending(listingApp.Contains).ToList();
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
