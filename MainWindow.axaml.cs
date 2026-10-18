using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

public partial class MainWindow : Window
{
    private AppConfig _config = new();
    private List<DiscoveredAddon> _discoveredAddons = new();
    private bool _isProcessing;
    private bool _isInitializing = true;
    private bool _isUpdatingSelection = false;
    private bool _isApplyingPreset;
    private IReadOnlyDictionary<string, string>? _selectedNamedGraphs;
    private List<HeroPresetChoice> _presetChoices = new();
    private IReadOnlyList<NpcPreset> _scannedNpcPresets = [];
    private Dictionary<string, Avalonia.Media.Imaging.Bitmap> _npcIcons = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Bitmap> _presetPortraits = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _portraitLoadGate = new(1, 1);
    private string? _portraitSource;
    private bool _logFlushPending;
    private int _previewRequest;
    private CancellationTokenSource? _compileCancellation;
    private readonly System.Text.StringBuilder _logBuffer = new();
    private readonly List<string> _pendingLogLines = new();
    private readonly Queue<int> _logLineInlines = new();
    private const int MaxLogLines = 3000;
    private readonly ProtectedModelRegistry _protected;

    private static readonly IBrush BrushOk = new SolidColorBrush(Color.FromRgb(0xDF, 0xE2, 0xE6));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.FromRgb(0xBA, 0xBE, 0xC4));
    private static readonly IBrush BrushErr = new SolidColorBrush(Color.FromRgb(0xDF, 0x70, 0x70));
    private static readonly IBrush BrushMuted = new SolidColorBrush(Color.FromRgb(0x82, 0x86, 0x8E));
    private static readonly IBrush LogTime = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly IBrush LogText = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
    private static readonly IBrush LogError = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
    private static readonly IBrush LogWarning = new SolidColorBrush(Color.FromRgb(0xFC, 0xD9, 0x9B));
    private static readonly IBrush LogSuccess = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));

    public MainWindow()
    {
        InitializeComponent();
        _protected = new ProtectedModelRegistry(Log);
        _protected.Changed += UpdateProtectionStatus;
        RestoreWindowLayout(ConfigManager.LoadConfig());
        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => BorderDropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        AppLog.Message += Log;
        Closed += (_, _) => AppLog.Message -= Log;
        Loaded += MainWindow_Loaded;
        Closing += (_, _) => SaveWindowLayout();
        Closed += (_, _) =>
        {
            _protected.Dispose();
            foreach (var portrait in _presetPortraits.Values)
                portrait.Dispose();
        };
    }

    // -----------------------------------------------------------------
    // WINDOW LAYOUT AND DRAG & DROP
    // -----------------------------------------------------------------
    private void RestoreWindowLayout(AppConfig config)
    {
        if (config.WindowWidth is >= 960 and <= 8000 && config.WindowHeight is >= 620 and <= 8000)
        {
            Width = config.WindowWidth.Value;
            Height = config.WindowHeight.Value;
        }
        if (config.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowLayout()
    {
        if (_isInitializing) return;
        _config.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _config.WindowWidth = Math.Round(Bounds.Width);
            _config.WindowHeight = Math.Round(Bounds.Height);
        }
        ConfigManager.SaveConfig(_config);
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _isInitializing = true;
            _isUpdatingSelection = true;

            _config = ConfigManager.LoadConfig();

            // Populate presets
            PopulateHeroPresets();
            if (HeroDatabase.CustomLoadError != null) Log($"[ag2 presets] {HeroDatabase.CustomLoadError}");

            // Apply config to UI
            TxtCsWinPath.Text = _config.CsWinDir ?? string.Empty;
            TxtCitadelPath.Text = _config.CitadelAddonsDir ?? string.Empty;

            ChkRevert.IsChecked = _config.ChkRevert;
            ChkSkel.IsChecked = _config.ChkSkel;
            ChkGraph.IsChecked = _config.ChkGraph;
            ChkUiGraph.IsChecked = _config.ChkUiGraph;
            ChkDisableAnimList.IsChecked = _config.ChkDisableAnimList;
            ChkAutoDetectAnims.IsChecked = _config.ChkAutoDetectAnims;
            ChkSkipMaterialCompilation.IsChecked = _config.ChkSkipMaterialCompilation;

            ChkRevert.IsCheckedChanged += (_, _) => SaveConfig();
            ChkSkel.IsCheckedChanged += (_, _) => SaveConfig();
            ChkGraph.IsCheckedChanged += (_, _) => SaveConfig();
            ChkUiGraph.IsCheckedChanged += (_, _) => SaveConfig();
            ChkDisableAnimList.IsCheckedChanged += (_, _) => SaveConfig();
            ChkAutoDetectAnims.IsCheckedChanged += (_, _) => SaveConfig();
            ChkSkipMaterialCompilation.IsCheckedChanged += (_, _) => SaveConfig();

            // Environment validation on startup
            ValidateEnvironmentOnStartup();

            var deadlockInstall = DeadlockLocator.DetectDeadlockInstallation();
            if (deadlockInstall.IsValid)
            {
                _ = LoadPresetPortraitsAsync(deadlockInstall.Pak01VpkPath);
                _ = LoadNpcPresetsAsync(deadlockInstall.Pak01VpkPath);
            }

            RescanModels(logOutput: false);

            var resolved = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                UpdateHeroDetailsFromPath(resolved);
                _ = Init3DSceneAsync(resolved);
            }

            Log("environment verified. ready.");

            // First-time setup wizard
            var csValid = CsWinCompiler.IsValidCsWinDir(TxtCsWinPath.Text?.Trim());
            var citValid = !string.IsNullOrWhiteSpace(TxtCitadelPath.Text?.Trim()) && Directory.Exists(TxtCitadelPath.Text?.Trim());

            if (!csValid || !citValid)
            {
                await PromptFirstTimeSetupAsync(!csValid, !citValid);
            }

            _ = CheckForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Log($"[init error] {ex.Message}");
        }
        finally
        {
            _isUpdatingSelection = false;
            _isInitializing = false;
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var result = await UpdateService.CheckAsync();
            if (!result.UpdateAvailable)
                return;

            if (!result.Success || string.IsNullOrWhiteSpace(result.AssetUrl))
            {
                Log($"[update] {result.Message}");
                if (IsVisible)
                {
                    await DialogService.ShowInfoAsync(this, "update available",
                        $"version {result.LatestVersion} is available, but no downloadable Windows asset was found.\n\n" +
                        (result.ReleaseUrl ?? "open the project releases page to download it."));
                }
                return;
            }

            // Remember a user's decision for this exact release. A newer
            // release will still be offered later.
            var latestVersionKey = result.LatestVersion?.ToString();
            if (!string.IsNullOrWhiteSpace(latestVersionKey) &&
                string.Equals(_config.UpdateDismissedVersion, latestVersionKey, StringComparison.OrdinalIgnoreCase))
            {
                Log($"[update] version {latestVersionKey} was previously declined; skipping notification.");
                return;
            }

            if (!IsVisible || !await DialogService.ShowConfirmAsync(this, "update available",
                    $"version {result.LatestVersion} is available.\n\nWould you like to download it now?"))
            {
                Log($"[update] version {result.LatestVersion} available; download declined.");
                _config.UpdateDismissedVersion = latestVersionKey;
                ConfigManager.SaveConfig(_config);
                return;
            }

            var download = await UpdateService.DownloadAsync(result);
            if (!download.Success || string.IsNullOrWhiteSpace(download.DownloadedExecutablePath))
            {
                Log($"[update] {download.Message}");
                await DialogService.ShowInfoAsync(this, "update download failed", download.Message);
                return;
            }

            Log($"[update] downloaded version {download.LatestVersion}: {download.DownloadedExecutablePath}");
            var restart = await DialogService.ShowConfirmAsync(this, "update downloaded",
                $"version {download.LatestVersion} was downloaded next to the current program.\n\n" +
                "restart now to apply the update?");
            if (restart && UpdateService.TryStartRestartWithUpdate(download.DownloadedExecutablePath))
            {
                Environment.Exit(0);
            }

            if (restart)
            {
                await DialogService.ShowErrorAsync(this, "update restart failed",
                    "the update was downloaded, but the program could not restart automatically.\n" +
                    $"close this copy and run:\n{download.DownloadedExecutablePath}");
            }
        }
        catch (Exception ex)
        {
            Log($"[update] check failed: {ex.Message}");
        }
    }

    private void BtnToggleAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        PanelAdvancedContent.IsVisible = !PanelAdvancedContent.IsVisible;
        IconAdvancedChevron.Data = (Geometry)this.FindResource(
            PanelAdvancedContent.IsVisible ? "IconChevronUp" : "IconChevronDown")!;
    }

    private async Task PromptFirstTimeSetupAsync(bool needCsWin, bool needCitadel)
    {
        await DialogService.ShowInfoAsync(
            this,
            "initial setup",
            "welcome! please configure your cswin64 compiler and citadel addons directory to get started."
        );

        if (needCsWin)
        {
            var csFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "select cswin64 directory (containing resourcecompiler.exe)"
            });

            if (csFolders.Count > 0)
            {
                TxtCsWinPath.Text = csFolders[0].Path.LocalPath;
                Log($"[setup] cswin64 path configured: {csFolders[0].Path.LocalPath}");
                SaveConfig();
            }
        }

        if (needCitadel)
        {
            var citFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "select citadel addons directory (content/citadel_addons)"
            });

            if (citFolders.Count > 0)
            {
                TxtCitadelPath.Text = citFolders[0].Path.LocalPath;
                Log($"[setup] citadel addons path configured: {citFolders[0].Path.LocalPath}");
                SaveConfig();
                RescanModels();
            }
        }

        CheckEnvironmentStatus();
    }

    // -----------------------------------------------------------------
    // ENVIRONMENT & MODEL DETECTION LOGIC
    // -----------------------------------------------------------------
    private void CheckEnvironmentStatus()
    {
        try
        {
            var csWinDir = TxtCsWinPath.Text?.Trim() ?? string.Empty;
            var isValidCsWin = CsWinCompiler.IsValidCsWinDir(csWinDir);

            if (isValidCsWin)
            {
                LblCsWinStatus.Text = "ready (compiler found)";
                LblCsWinStatus.Foreground = BrushOk;
                if (BorderCsWinCheck != null)
                {
                    BorderCsWinCheck.IsVisible = true;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x3E, 0x2B));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    IconCsWinStatus.Data = Geometry.Parse("M9 16.2L4.8 12L3.4 13.4L9 19L21 7L19.6 5.6L9 16.2Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                }
            }
            else
            {
                LblCsWinStatus.Text = "compiler missing";
                LblCsWinStatus.Foreground = BrushErr;
                if (BorderCsWinCheck != null)
                {
                    BorderCsWinCheck.IsVisible = true;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x1A, 0x1A));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    IconCsWinStatus.Data = Geometry.Parse("M19 6.41L17.59 5L12 10.59L6.41 5L5 6.41L10.59 12L5 17.59L6.41 19L12 13.41L17.59 19L19 17.59L13.41 12L19 6.41Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                }
            }

            var citadelDir = TxtCitadelPath.Text?.Trim() ?? string.Empty;
            var isValidCitadel = !string.IsNullOrWhiteSpace(citadelDir) && Directory.Exists(citadelDir);

            if (isValidCitadel)
            {
                LblCitadelStatus.Text = "connected to addons folder";
                LblCitadelStatus.Foreground = BrushOk;
            }
            else
            {
                LblCitadelStatus.Text = "addons folder not found";
                LblCitadelStatus.Foreground = BrushErr;
            }
        }
        catch (Exception ex)
        {
            Log($"[environment] could not check the paths: {ex.Message}");
        }
    }

    private void ValidateEnvironmentOnStartup()
    {
        var citadelDir = TxtCitadelPath.Text?.Trim();
        var csWinDir = TxtCsWinPath.Text?.Trim();

        bool citadelValid = !string.IsNullOrWhiteSpace(citadelDir) && Directory.Exists(citadelDir);
        bool csWinValid = CsWinCompiler.IsValidCsWinDir(csWinDir);

        if (!citadelValid || !csWinValid)
        {
            var info = DeadlockLocator.DetectDeadlockInstallation();
            if (info.IsValid && !citadelValid)
            {
                // Workshop tools installed into the game keep addons beside it, named after its content folder.
                var candAddons = Path.Combine(info.GameRootPath, "content", info.ModDirectoryName + "_addons");
                if (Directory.Exists(candAddons))
                {
                    TxtCitadelPath.Text = candAddons;
                    Log($"[auto-detect] citadel addons directory: {candAddons}");
                }
            }
        }

        CheckEnvironmentStatus();
    }

    private void SaveConfig()
    {
        if (_isInitializing || _isApplyingPreset) return;

        _config.CsWinDir = TxtCsWinPath.Text?.Trim() ?? string.Empty;
        _config.CitadelAddonsDir = TxtCitadelPath.Text?.Trim() ?? string.Empty;
        _config.ChkRevert = ChkRevert.IsChecked == true;
        _config.ChkSkel = ChkSkel.IsChecked == true;
        _config.ChkGraph = ChkGraph.IsChecked == true;
        if (ChkUiGraph.IsEnabled) _config.ChkUiGraph = ChkUiGraph.IsChecked == true;
        _config.ChkDisableAnimList = ChkDisableAnimList.IsChecked == true;
        _config.ChkAutoDetectAnims = ChkAutoDetectAnims.IsChecked == true;
        _config.ChkSkipMaterialCompilation = ChkSkipMaterialCompilation.IsChecked == true;

        if (!ConfigManager.SaveConfig(_config))
        {
            Log("[config error] configuration could not be saved.");
        }
    }

    private void TxtCsWinPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        CheckEnvironmentStatus();
        SaveConfig();
    }

    private void TxtCitadelPath_TextChanged(object? sender, TextChangedEventArgs e)
    {
        CheckEnvironmentStatus();
        SaveConfig();
        if (!_isInitializing) RescanModels(logOutput: false);
    }

    private async void BrowseCsWin_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "select cswin64 directory (containing resourcecompiler.exe)"
        });

        if (folders.Count > 0)
        {
            TxtCsWinPath.Text = folders[0].Path.LocalPath;
            Log($"[setup] cswin64 path configured: {folders[0].Path.LocalPath}");
        }
    }

    private async void BrowseCitadel_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "select citadel addons directory (content/citadel_addons)"
        });

        if (folders.Count > 0)
        {
            TxtCitadelPath.Text = folders[0].Path.LocalPath;
            Log($"[setup] citadel addons path configured: {folders[0].Path.LocalPath}");
            RescanModels();
        }
    }

}
