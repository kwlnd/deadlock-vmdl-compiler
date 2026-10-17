using DeadlockVmdlCompiler.Services;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

public class SteamDetectionTests
{
    private static void CreateSteamInstallation(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, "game", "bin", "win64"));
        Directory.CreateDirectory(Path.Combine(directory, "game", "citadel"));
        // A launcher embeds the name of its content folder; engine tools beside it do not.
        File.WriteAllBytes(Path.Combine(directory, "game", "bin", "win64", "deadlock.exe"),
            System.Text.Encoding.Unicode.GetBytes("launcher for citadel"));
        File.WriteAllText(Path.Combine(directory, "game", "bin", "win64", "aaa_tool.exe"), "tool");
        File.WriteAllText(Path.Combine(directory, "game", "citadel", "pak01_dir.vpk"), "fixture");
        File.WriteAllText(Path.Combine(directory, "game", "citadel", "steam.inf"), "ClientVersion=1\nappID=1422450\n");
        // Language folders and other games carry their own steam.inf or none at all.
        Directory.CreateDirectory(Path.Combine(directory, "game", "core"));
        File.WriteAllText(Path.Combine(directory, "game", "core", "pak01_dir.vpk"), "fixture");
        File.WriteAllText(Path.Combine(directory, "game", "core", "steam.inf"), "appID=730\n");
    }

    private static string SteamManifest(string installDir, string appId = "1422450") =>
        $"\"AppState\" {{ \"appid\" \"{appId}\" // installdir is defined below\n \"installdir\" \"{installDir}\" }}";

    private static string VdfPath(string path) => path.Replace("\\", "\\\\");

    [Fact]
    public void FindsTheGameThroughLibrariesAndManifests()
    {
        using var temp = new TempDir();
        var steamRoot = temp["unusual client location"];
        var secondaryLibrary = temp["Другая библиотека"];
        var legacyLibrary = temp["legacy library"];
        foreach (var library in new[] { steamRoot, secondaryLibrary, legacyLibrary })
            Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        Directory.CreateDirectory(Path.Combine(steamRoot, "config"));
        var renamedGame = Path.Combine(secondaryLibrary, "steamapps", "common", "custom game folder");
        var legacyGame = Path.Combine(legacyLibrary, "steamapps", "common", "game");
        CreateSteamInstallation(renamedGame);
        CreateSteamInstallation(legacyGame);
        CreateSteamInstallation(Path.Combine(steamRoot, "steamapps", "common", "Deadlock"));
        var primaryManifest = Path.Combine(steamRoot, "steamapps", "appmanifest_1422450.acf");
        var secondaryManifest = Path.Combine(secondaryLibrary, "steamapps", "appmanifest_1422450.acf");
        var legacyManifest = Path.Combine(legacyLibrary, "steamapps", "appmanifest_1422450.acf");
        File.WriteAllText(primaryManifest, SteamManifest("no longer installed"));
        File.WriteAllText(secondaryManifest, SteamManifest("custom game folder"));
        File.WriteAllText(legacyManifest, SteamManifest("game"));
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{VdfPath(steamRoot)}\" }} " +
            $"\"1\" {{ \"path\" \"{VdfPath(secondaryLibrary)}\" \"apps\" {{ }} }} " +
            $"\"2\" {{ \"path\" \"{VdfPath(secondaryLibrary)}\" }} " +
            "// \"path\" \"Z:\\\\ignored comment\"\n }");
        File.WriteAllText(Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
            $"\"LibraryFolders\" {{ \"TimeNextStatsReport\" \"0\" \"1\" \"{VdfPath(legacyLibrary)}\" }}");

        var steamLibraries = DeadlockLocator.GetSteamLibraryFolders(new[] { steamRoot });
        Check(steamLibraries.Count == 3 && steamLibraries.Contains(secondaryLibrary) && steamLibraries.Contains(legacyLibrary),
            "Steam libraries were not discovered from modern and legacy VDF metadata with escaped paths.");
        var detected = DeadlockLocator.DetectFromSteamLibraries(steamLibraries);
        Check(detected.GameRootPath == renamedGame,
            "Steam detection guessed a folder name or failed to use installdir from the secondary library manifest.");
        Check(detected.ModDirectoryName == "citadel" &&
              Path.GetFileName(detected.DeadlockExePath) == "deadlock.exe" &&
              detected.Pak01VpkPath == Path.Combine(renamedGame, "game", "citadel", "pak01_dir.vpk"),
            "The game folder was not identified by its steam.inf, or an engine tool was taken for the launcher.");

        var relocated = temp["relocated install"];
        CreateSteamInstallation(relocated);
        Directory.Move(Path.Combine(relocated, "game", "citadel"), Path.Combine(relocated, "game", "renamed_mod"));
        File.WriteAllBytes(Path.Combine(relocated, "game", "bin", "win64", "deadlock.exe"),
            System.Text.Encoding.Unicode.GetBytes("launcher for renamed_mod"));
        var relocatedInfo = DeadlockLocator.ValidateAndExtractInfo(relocated);
        Check(relocatedInfo.IsValid && relocatedInfo.ModDirectoryName == "renamed_mod" &&
              relocatedInfo.Pak01VpkPath == Path.Combine(relocated, "game", "renamed_mod", "pak01_dir.vpk"),
            "Detection depends on the content folder being called citadel.");
        File.Delete(Path.Combine(relocated, "game", "renamed_mod", "steam.inf"));
        Check(!DeadlockLocator.ValidateAndExtractInfo(relocated).IsValid,
            "A folder without the game's own steam.inf was accepted as Deadlock.");
        Check(DeadlockLocator.ValidateAndExtractInfo(Path.Combine(renamedGame, "game", "citadel", "pak01_dir.vpk")).GameRootPath == renamedGame,
            "An explicit game VPK hint was not normalized to the installation root.");

        File.WriteAllText(primaryManifest, "\"AppState\" { \"appid\"");
        File.WriteAllText(secondaryManifest, SteamManifest("custom game folder", "730"));
        Check(DeadlockLocator.DetectFromSteamLibraries(steamLibraries).GameRootPath == legacyGame,
            "Malformed or wrong-app manifests prevented detection, or an installdir named game was misnormalized.");
        var outsideGame = Path.Combine(secondaryLibrary, "outside");
        CreateSteamInstallation(outsideGame);
        File.WriteAllText(secondaryManifest, SteamManifest("../../outside"));
        File.Delete(legacyManifest);
        Check(!DeadlockLocator.DetectFromSteamLibraries(steamLibraries).IsValid,
            "Steam detection accepted a path outside common or guessed an installation without a valid app manifest.");
        File.WriteAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" {");
        File.WriteAllText(primaryManifest, SteamManifest("Deadlock"));
        Check(DeadlockLocator.DetectFromSteamLibraries(DeadlockLocator.GetSteamLibraryFolders(new[] { steamRoot })).IsValid,
            "A damaged library list prevented detection in the primary Steam library.");
    }

    [Fact]
    public void FindsTheInstalledGame()
    {
        RequireFlag("DEADLOCK_TEST_STEAM");
        Check(DeadlockLocator.DetectDeadlockInstallation().IsValid,
            "The real Steam installation was not found through its library list and app manifest.");
    }
}
