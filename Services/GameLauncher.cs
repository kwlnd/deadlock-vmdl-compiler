using System;
using System.Diagnostics;
using System.IO;

namespace DeadlockVmdlCompiler.Services;

public static class GameLauncher
{
    public static (bool Success, string Message) LaunchDeadlockGame(string addonName, string? hintDeadlockDir = null)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return (false, "No addon specified for testing in Deadlock.");

        var install = DeadlockLocator.DetectDeadlockInstallation(hintDeadlockDir);
        if (!install.IsValid || !File.Exists(install.DeadlockExePath))
        {
            return (false, "Deadlock installation could not be detected automatically via Steam.");
        }

        try
        {
            var workingDir = Path.GetDirectoryName(install.DeadlockExePath)!;
            var args = $"-addon {addonName}";

            var psi = new ProcessStartInfo
            {
                FileName = install.DeadlockExePath,
                Arguments = args,
                WorkingDirectory = workingDir,
                UseShellExecute = true
            };

            Process.Start(psi);
            return (true, $"Launched Deadlock with addon: [{addonName}]");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch Deadlock: {ex.Message}");
        }
    }
}
