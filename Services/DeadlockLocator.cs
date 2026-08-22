using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

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
    public static DeadlockInstallInfo DetectDeadlockInstallation(string? hintPath = null)
    {
        // 1. If a hint path was provided, validate it first
        if (!string.IsNullOrWhiteSpace(hintPath))
        {
            var fromHint = ValidateAndExtractInfo(hintPath);
            if (fromHint.IsValid) return fromHint;
        }

        // 2. Try Steam Registry & libraryfolders.vdf
        var steamLibraries = GetSteamLibraryFolders();
        foreach (var lib in steamLibraries)
        {
            var cand1 = Path.Combine(lib, "steamapps", "common", "Deadlock");
            var cand2 = Path.Combine(lib, "steamapps", "common", "Citadel");
            var cand3 = Path.Combine(lib, "steamapps", "common", "deadlock");

            var info1 = ValidateAndExtractInfo(cand1);
            if (info1.IsValid) return info1;

            var info2 = ValidateAndExtractInfo(cand2);
            if (info2.IsValid) return info2;

            var info3 = ValidateAndExtractInfo(cand3);
            if (info3.IsValid) return info3;
        }

        // 3. Fallback common drive locations
        var commonRoots = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Deadlock",
            @"C:\Steam\steamapps\common\Deadlock",
            @"C:\SteamLibrary\steamapps\common\Deadlock",
            @"D:\SteamLibrary\steamapps\common\Deadlock",
            @"D:\Steam\steamapps\common\Deadlock",
            @"D:\Games\Steam\steamapps\common\Deadlock",
            @"E:\SteamLibrary\steamapps\common\Deadlock",
            @"E:\Steam\steamapps\common\Deadlock",
            @"F:\SteamLibrary\steamapps\common\Deadlock"
        };

        foreach (var c in commonRoots)
        {
            var info = ValidateAndExtractInfo(c);
            if (info.IsValid) return info;
        }

        return new DeadlockInstallInfo();
    }

    public static DeadlockInstallInfo ValidateAndExtractInfo(string candidateDir)
    {
        var info = new DeadlockInstallInfo();
        if (string.IsNullOrWhiteSpace(candidateDir) || !Directory.Exists(candidateDir))
            return info;

        var full = Path.GetFullPath(candidateDir);

        // Normalize if user picked a subfolder like /game, /game/bin/win64, or /game/citadel
        string root = full;
        var clean = full.Replace('\\', '/');
        if (clean.EndsWith("/game/bin/win64", StringComparison.OrdinalIgnoreCase))
        {
            root = Directory.GetParent(Directory.GetParent(Directory.GetParent(full)!.FullName)!.FullName)!.FullName;
        }
        else if (clean.EndsWith("/game/citadel", StringComparison.OrdinalIgnoreCase))
        {
            root = Directory.GetParent(Directory.GetParent(full)!.FullName)!.FullName;
        }
        else if (clean.EndsWith("/game", StringComparison.OrdinalIgnoreCase))
        {
            root = Directory.GetParent(full)!.FullName;
        }

        var exeCand1 = Path.Combine(root, "game", "bin", "win64", "deadlock.exe");
        var exeCand2 = Path.Combine(root, "bin", "win64", "deadlock.exe");
        var exeCand3 = Path.Combine(root, "game", "bin", "win64", "project8.exe");

        string? foundExe = null;
        if (File.Exists(exeCand1)) foundExe = exeCand1;
        else if (File.Exists(exeCand2)) foundExe = exeCand2;
        else if (File.Exists(exeCand3)) foundExe = exeCand3;

        var vpkCand1 = Path.Combine(root, "game", "citadel", "pak01_dir.vpk");
        var vpkCand2 = Path.Combine(root, "citadel", "pak01_dir.vpk");
        var vpkCand3 = Path.Combine(root, "pak01_dir.vpk");

        string? foundVpk = null;
        if (File.Exists(vpkCand1)) foundVpk = vpkCand1;
        else if (File.Exists(vpkCand2)) foundVpk = vpkCand2;
        else if (File.Exists(vpkCand3)) foundVpk = vpkCand3;

        if (foundExe != null && foundVpk != null)
        {
            info.GameRootPath = root;
            info.DeadlockExePath = foundExe;
            info.Pak01VpkPath = foundVpk;
        }

        return info;
    }

    public static List<string> GetSteamLibraryFolders()
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Steam path from HKCU Registry
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
            {
                var norm = steamPath.Replace('/', '\\');
                if (Directory.Exists(norm)) libraries.Add(norm);
            }
        }
        catch { }

        // 2. Steam path from HKLM Registry
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam") ??
                            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Valve\Steam");
            if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
            {
                var norm = installPath.Replace('/', '\\');
                if (Directory.Exists(norm)) libraries.Add(norm);
            }
        }
        catch { }

        // 3. Steam from running process
        try
        {
            var proc = Process.GetProcessesByName("steam").FirstOrDefault();
            if (proc?.MainModule?.FileName is string exePath)
            {
                var dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    libraries.Add(dir);
                }
            }
        }
        catch { }

        // 4. Parse libraryfolders.vdf from each discovered Steam folder
        var initialSteamPaths = libraries.ToList();
        foreach (var sp in initialSteamPaths)
        {
            var vdfPath = Path.Combine(sp, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    var text = File.ReadAllText(vdfPath);
                    var matches = Regex.Matches(text, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                    {
                        var libPath = m.Groups[1].Value.Replace(@"\\", @"\").Trim();
                        if (Directory.Exists(libPath))
                        {
                            libraries.Add(libPath);
                        }
                    }
                }
                catch { }
            }
        }

        return libraries.ToList();
    }
}
