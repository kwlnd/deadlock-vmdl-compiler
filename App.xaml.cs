using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);
    private const int AttachParentProcess = -1;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            LogCrash("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // Prevent crash, keep app alive!
            MessageBox.Show($"An unexpected error occurred:\n{args.Exception.Message}\n\nDetails saved to crash.log", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        var args = e.Args;
        if (args.Length > 0 && (args.Contains("--file") || args.Contains("-f") || args.Contains("--dir") || args.Contains("-d") || args.Contains("--save-config") || args.Contains("-h") || args.Contains("--help")))
        {
            AttachConsole(AttachParentProcess);
            RunCliAsync(args).GetAwaiter().GetResult();
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        this.MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}]\n{ex?.ToString()}\n\n";
            File.AppendAllText(logPath, text);
        }
        catch { }
    }

    private static async Task RunCliAsync(string[] args)
    {
        var cfg = ConfigManager.LoadConfig();
        string? file = null;
        string? dir = null;
        string? cswinDir = cfg.CsWinDir;
        string? citadelDir = cfg.CitadelAddonsDir;
        string? hero = null;
        string? skel = null;
        string? graph = null;
        string? uiGraph = null;
        bool saveConfig = false;
        bool noBackup = false;
        bool noCompile = false;
        bool noRevert = false;
        bool noHeader = false;
        bool noSkel = false;
        bool noGraph = false;
        bool noUiGraph = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();
            if ((a == "--file" || a == "-f") && i + 1 < args.Length) file = args[++i];
            else if ((a == "--dir" || a == "-d") && i + 1 < args.Length) dir = args[++i];
            else if (a == "--cswin-dir" && i + 1 < args.Length) cswinDir = args[++i];
            else if (a == "--citadel-dir" && i + 1 < args.Length) citadelDir = args[++i];
            else if ((a == "--hero" || a == "-hp") && i + 1 < args.Length) hero = args[++i];
            else if (a == "--skel" && i + 1 < args.Length) skel = args[++i];
            else if (a == "--graph" && i + 1 < args.Length) graph = args[++i];
            else if (a == "--ui-graph" && i + 1 < args.Length) uiGraph = args[++i];
            else if (a == "--save-config") saveConfig = true;
            else if (a == "--no-backup") noBackup = true;
            else if (a == "--no-compile") noCompile = true;
            else if (a == "--no-revert") noRevert = true;
            else if (a == "--no-header") noHeader = true;
            else if (a == "--no-skel") noSkel = true;
            else if (a == "--no-graph") noGraph = true;
            else if (a == "--no-ui-graph") noUiGraph = true;
            else if (a == "-h" || a == "--help")
            {
                Console.WriteLine("Deadlock AG2 VMDL Compiler (CSDK12)");
                Console.WriteLine("Usage:");
                Console.WriteLine("  --file, -f <path>        Process a single .vmdl file");
                Console.WriteLine("  --dir, -d <path>         Recursively process .vmdl files in a directory");
                Console.WriteLine("  --cswin-dir <path>       Path to CSWin64 installation");
                Console.WriteLine("  --citadel-dir <path>     Path to CSDK12 citadel_addons directory");
                Console.WriteLine("  --save-config            Save paths to config.json");
                Console.WriteLine("  --hero <name>            Hero preset name");
                return;
            }
        }

        if (saveConfig)
        {
            if (!string.IsNullOrEmpty(cswinDir)) cfg.CsWinDir = cswinDir;
            if (!string.IsNullOrEmpty(citadelDir)) cfg.CitadelAddonsDir = citadelDir;
            ConfigManager.SaveConfig(cfg);
            Console.WriteLine($"Saved configuration to {ConfigManager.GetConfigPath()}");
        }

        var targets = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
            targets.Add(file);

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.vmdl", SearchOption.AllDirectories))
                targets.Add(f);
        }

        if (targets.Count == 0)
        {
            if (!saveConfig)
                Console.WriteLine("No .vmdl files found or specified.");
            return;
        }

        var db = HeroDatabase.GetDatabase();
        if (!string.IsNullOrEmpty(hero) && db.TryGetValue(hero, out var preset))
        {
            skel ??= preset.Skel;
            graph ??= preset.Graph;
            uiGraph ??= preset.UiGraph;
        }

        Console.WriteLine($"Processing {targets.Count} file(s)...");
        foreach (var target in targets)
        {
            var (success, msg) = await VmdlPipeline.ProcessVmdlFileAsync(
                target,
                skelPath: skel,
                graphPath: graph,
                uiGraphPath: uiGraph,
                createBackup: !noBackup,
                addSkel: !noSkel,
                addGraph: !noGraph,
                addUiGraph: !noUiGraph,
                upgradeHeader: !noHeader,
                compileCsWin: !noCompile,
                revertVmdl: !noRevert,
                cswinDir: cswinDir,
                citadelAddonsDir: citadelDir
            );

            var status = success ? "SUCCESS" : "FAILED";
            Console.WriteLine($"[{status}] {target}\n        {msg}");
        }
    }
}
