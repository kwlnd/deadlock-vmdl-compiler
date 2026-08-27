using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

    private static readonly IBrush BrushOk = new SolidColorBrush(Color.FromRgb(0xDF, 0xE2, 0xE6));
    private static readonly IBrush BrushWarn = new SolidColorBrush(Color.FromRgb(0xBA, 0xBE, 0xC4));
    private static readonly IBrush BrushErr = new SolidColorBrush(Color.FromRgb(0xDF, 0x70, 0x70));
    private static readonly IBrush BrushMuted = new SolidColorBrush(Color.FromRgb(0x82, 0x86, 0x8E));

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _isInitializing = true;
            _isUpdatingSelection = true;

            _config = ConfigManager.LoadConfig();

            // Populate presets
            var presets = new List<string> { "(Auto-Detect Hero Paths)" };
            presets.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
            CmbHeroPreset.ItemsSource = presets;
            CmbHeroPreset.SelectedIndex = 0;

            // Apply config to UI
            TxtCsWinPath.Text = _config.CsWinDir ?? string.Empty;
            TxtCitadelPath.Text = _config.CitadelAddonsDir ?? string.Empty;

            ChkRevert.IsChecked = _config.ChkRevert;
            ChkSkel.IsChecked = _config.ChkSkel;
            ChkGraph.IsChecked = _config.ChkGraph;
            ChkUiGraph.IsChecked = _config.ChkUiGraph;

            // Environment validation on startup
            ValidateEnvironmentOnStartup();

            RescanModels(logOutput: false);

            var resolved = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                UpdateHeroDetailsFromPath(resolved);
                _ = Init3DSceneAsync(resolved);
            }

            Log("Environment verified. Ready.");
        }
        catch (Exception ex)
        {
            Log($"[Init Error] {ex.Message}");
        }
        finally
        {
            _isUpdatingSelection = false;
            _isInitializing = false;
        }
    }

    private void Log(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            TxtLog.Text = (TxtLog.Text ?? string.Empty) + $"[{timestamp}] {message}\n";
            if (ChkAutoScroll?.IsChecked == true)
            {
                ScrollLog?.ScrollToEnd();
            }
        });
    }

    // -----------------------------------------------------------------
    // 3D VIEWPORT LOGIC
    // -----------------------------------------------------------------
    private async Task Init3DSceneAsync(string? targetPath = null)
    {
        try
        {
            var path = targetPath ?? GetResolvedTargetPath();

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblMeshName.Text = "mesh: loading preview...";
                var mesh = await DmxModelLoader.LoadModelFromVmdlAsync(path);
                ModelViewport.CurrentMesh = mesh;

                if (mesh != null)
                {
                    LblMeshName.Text = $"mesh: {mesh.MeshName} ({mesh.Vertices.Count:N0} verts, {mesh.Indices.Count / 3:N0} tris)";
                }
                else
                {
                    LblMeshName.Text = $"mesh: {Path.GetFileName(path)} (render mesh not found)";
                }
            }
            else
            {
                ModelViewport.CurrentMesh = null;
                LblMeshName.Text = "mesh: no model selected";
            }
        }
        catch { }
    }

    // -----------------------------------------------------------------
    // ENVIRONMENT & MODEL DETECTION LOGIC
    // -----------------------------------------------------------------
    private void CheckEnvironmentStatus()
    {
        try
        {
            var csWinDir = TxtCsWinPath.Text?.Trim() ?? string.Empty;
            var isValidCsWin = VmdlPipeline.IsValidCsWinDir(csWinDir);

            if (isValidCsWin)
            {
                LblCsWinStatus.Text = "Ready (compiler found)";
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
                LblCsWinStatus.Text = "Compiler missing";
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
                LblCitadelStatus.Text = "Connected to addons folder";
                LblCitadelStatus.Foreground = BrushOk;
            }
            else
            {
                LblCitadelStatus.Text = "Addons folder not found";
                LblCitadelStatus.Foreground = BrushErr;
            }
        }
        catch { }
    }

    private void ValidateEnvironmentOnStartup()
    {
        var citadelDir = TxtCitadelPath.Text?.Trim();
        var csWinDir = TxtCsWinPath.Text?.Trim();

        bool citadelValid = !string.IsNullOrWhiteSpace(citadelDir) && Directory.Exists(citadelDir);
        bool csWinValid = VmdlPipeline.IsValidCsWinDir(csWinDir);

        if (!citadelValid || !csWinValid)
        {
            var info = DeadlockLocator.DetectDeadlockInstallation();
            if (info.IsValid && !citadelValid)
            {
                var candAddons = Path.Combine(info.GameRootPath, "content", "citadel_addons");
                if (Directory.Exists(candAddons))
                {
                    TxtCitadelPath.Text = candAddons;
                    Log($"[AUTO-DETECT] Citadel addons directory: {candAddons}");
                }
            }
        }

        CheckEnvironmentStatus();
    }

    private void RescanModels(bool logOutput = true)
    {
        try
        {
            var citadelDir = TxtCitadelPath.Text?.Trim();
            if (string.IsNullOrWhiteSpace(citadelDir) || !Directory.Exists(citadelDir))
            {
                LblDiscoveredCount.Text = "addons folder not configured";
                LblDiscoveredCount.Foreground = BrushWarn;
                CmbDiscovered.ItemsSource = null;
                CmbTargetVmdl.ItemsSource = null;
                return;
            }

            var previousAddonName = (CmbDiscovered.SelectedItem as DiscoveredAddon)?.Name;

            _discoveredAddons = VmdlScanner.ScanAddons(citadelDir);

            var displayList = new List<DiscoveredAddon>();
            var placeholder = new DiscoveredAddon
            {
                Name = $"(Select addon: {_discoveredAddons.Count} available)",
                FullPath = string.Empty,
                HeroModels = new List<DiscoveredModel>(),
                IsPlaceholder = true,
                Display = $"(Select addon: {_discoveredAddons.Count} available)"
            };
            displayList.Add(placeholder);
            displayList.AddRange(_discoveredAddons);

            CmbDiscovered.ItemsSource = displayList;

            int totalModels = _discoveredAddons.Sum(a => a.HeroModels.Count);
            LblDiscoveredCount.Text = $"{_discoveredAddons.Count} addon(s) available";
            LblDiscoveredCount.Foreground = BrushMuted;

            if (logOutput)
            {
                Log($"Discovered {_discoveredAddons.Count} addon(s) in: {citadelDir}");
            }

            // Restore selection
            if (!string.IsNullOrEmpty(previousAddonName))
            {
                var match = _discoveredAddons.FirstOrDefault(a => a.Name.Equals(previousAddonName, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    CmbDiscovered.SelectedItem = match;
                    return;
                }
            }

            CmbDiscovered.SelectedIndex = _discoveredAddons.Count > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log($"[Scan Error] {ex.Message}");
        }
    }

    private string? GetResolvedTargetPath()
    {
        if (CmbTargetVmdl.SelectedItem is DiscoveredModel selectedModel)
        {
            return selectedModel.FullPath;
        }

        return null;
    }

    private void UpdateHeroDetailsFromPath(string vmdlPath)
    {
        try
        {
            var heroName = VmdlPipeline.DetectHeroFromPath(vmdlPath);
            if (string.IsNullOrEmpty(heroName)) return;

            var db = HeroDatabase.GetDatabase();
            if (db.TryGetValue(heroName, out var preset))
            {
                TxtSkel.Text = preset.Skel ?? string.Empty;
                TxtGraph.Text = preset.Graph ?? string.Empty;
                TxtUiGraph.Text = preset.UiGraph ?? string.Empty;

                if (CmbHeroPreset.ItemsSource is List<string> presets)
                {
                    var match = presets.FirstOrDefault(p => p.Equals(heroName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        CmbHeroPreset.SelectedItem = match;
                    }
                }
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        if (_isInitializing) return;

        _config.CsWinDir = TxtCsWinPath.Text?.Trim();
        _config.CitadelAddonsDir = TxtCitadelPath.Text?.Trim();
        _config.LastTargetPath = GetResolvedTargetPath();
        _config.ChkRevert = ChkRevert.IsChecked == true;
        _config.ChkSkel = ChkSkel.IsChecked == true;
        _config.ChkGraph = ChkGraph.IsChecked == true;
        _config.ChkUiGraph = ChkUiGraph.IsChecked == true;

        ConfigManager.SaveConfig(_config);
    }

    // -----------------------------------------------------------------
    // EVENT HANDLERS
    // -----------------------------------------------------------------
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

    private void CmbDiscovered_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;

        try
        {
            _isUpdatingSelection = true;
            if (CmbDiscovered.SelectedItem is DiscoveredAddon addon && addon.HeroModels.Count > 0)
            {
                CmbTargetVmdl.ItemsSource = addon.HeroModels;
                CmbTargetVmdl.SelectedIndex = 0;
            }
            else
            {
                CmbTargetVmdl.ItemsSource = null;
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        var path = GetResolvedTargetPath();
        if (!string.IsNullOrEmpty(path))
        {
            UpdateHeroDetailsFromPath(path);
            _ = Init3DSceneAsync(path);
        }
    }

    private void CmbTargetVmdl_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;

        var path = GetResolvedTargetPath();
        if (!string.IsNullOrEmpty(path))
        {
            UpdateHeroDetailsFromPath(path);
            _ = Init3DSceneAsync(path);
            Log($"Selected model: {Path.GetFileName(path)}");
        }
        SaveConfig();
    }

    private void CmbHeroPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbHeroPreset.SelectedItem is string presetName)
        {
            if (presetName == "(Auto-Detect Hero Paths)")
            {
                var path = GetResolvedTargetPath();
                if (!string.IsNullOrEmpty(path)) UpdateHeroDetailsFromPath(path);
            }
            else
            {
                var db = HeroDatabase.GetDatabase();
                if (db.TryGetValue(presetName, out var preset))
                {
                    TxtSkel.Text = preset.Skel ?? string.Empty;
                    TxtGraph.Text = preset.Graph ?? string.Empty;
                    TxtUiGraph.Text = preset.UiGraph ?? string.Empty;
                }
            }
        }
    }

    private async void BrowseCsWin_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select CSWin64 Directory (containing resourcecompiler.exe)"
        });

        if (folders.Count > 0)
        {
            TxtCsWinPath.Text = folders[0].Path.LocalPath;
            Log($"[SETUP] CSWin64 path configured: {folders[0].Path.LocalPath}");
        }
    }

    private async void BrowseCitadel_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Citadel Addons Directory (content/citadel_addons)"
        });

        if (folders.Count > 0)
        {
            TxtCitadelPath.Text = folders[0].Path.LocalPath;
            Log($"[SETUP] Citadel addons path configured: {folders[0].Path.LocalPath}");
            RescanModels();
        }
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Target .vmdl File",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Valve ModelDoc (*.vmdl)") { Patterns = new[] { "*.vmdl" } },
                new FilePickerFileType("All Files (*.*)") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            var fullPath = files[0].Path.LocalPath;
            var model = new DiscoveredModel
            {
                FullPath = fullPath,
                Filename = Path.GetFileName(fullPath),
                Display = Path.GetFileName(fullPath),
                Addon = "Manual"
            };
            CmbTargetVmdl.ItemsSource = new List<DiscoveredModel> { model };
            CmbTargetVmdl.SelectedIndex = 0;
            UpdateHeroDetailsFromPath(fullPath);
            _ = Init3DSceneAsync(fullPath);
            Log($"Manually loaded model: {Path.GetFileName(fullPath)}");
        }
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        RescanModels();
    }

    private async void BtnRestorePresets_Click(object? sender, RoutedEventArgs e)
    {
        var confirm = await DialogService.ShowConfirmAsync(this, "Restore Default Presets", "Are you sure you want to reset and restore the default hero preset database from built-in resources?");
        if (!confirm) return;

        var (success, msg, count) = HeroDatabase.RestoreOriginalDatabase();
        if (success)
        {
            Log($"Restored default hero preset database ({count} heroes).");
            var presets = new List<string> { "(Auto-Detect Hero Paths)" };
            presets.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
            CmbHeroPreset.ItemsSource = presets;
            CmbHeroPreset.SelectedIndex = 0;
            await DialogService.ShowInfoAsync(this, "Presets Restored", $"Successfully restored default hero preset database ({count} heroes).");
        }
        else
        {
            Log($"[Restore Error] {msg}");
            await DialogService.ShowErrorAsync(this, "Restore Failed", msg);
        }
    }

    private async void BtnUpdateVpkPresets_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        try
        {
            _isProcessing = true;
            Log("Scanning Deadlock VPK files for updated hero paths...");

            var citadelDir = TxtCitadelPath.Text?.Trim() ?? string.Empty;
            var (success, msg, presets) = await VpkHeroScanner.ScanVpkForHeroesAsync(citadelDir);
            if (success && presets.Count > 0)
            {
                HeroDatabase.SaveDatabase(presets);
                Log($"VPK Scan Complete: Updated {presets.Count} hero presets.");
                var list = new List<string> { "(Auto-Detect Hero Paths)" };
                list.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
                CmbHeroPreset.ItemsSource = list;
                CmbHeroPreset.SelectedIndex = 0;
                await DialogService.ShowInfoAsync(this, "VPK Presets Updated", $"VPK Scan Complete!\nSuccessfully extracted and updated {presets.Count} hero presets from Deadlock VPK.");
            }
            else
            {
                Log($"[VPK Scan Notice] {msg}");
                await DialogService.ShowErrorAsync(this, "VPK Scan Notice", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[VPK Scan Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "VPK Scan Error", ex.Message);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private async void BtnSanitizeModelDoc_Click(object? sender, RoutedEventArgs e)
    {
        var targetPath = GetResolvedTargetPath();
        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[Fix ModelDoc] Please select a target .vmdl file first.");
            await DialogService.ShowErrorAsync(this, "Selection Required", "Please select a target .vmdl model file first.");
            return;
        }

        try
        {
            var (success, msg) = await VmdlPipeline.SanitizeVmdlForModelDocAsync(targetPath);
            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, "ModelDoc Fixed", msg);
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "ModelDoc Fix Failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[Fix ModelDoc Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "Fix ModelDoc Error", ex.Message);
        }
    }

    private async void BtnMakeVpk_Click(object? sender, RoutedEventArgs e)
    {
        var targetPath = GetResolvedTargetPath();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(citadelDir))
        {
            Log("[Make VPK] Please select an addon and target model first.");
            await DialogService.ShowErrorAsync(this, "Selection Required", "Please select an addon and target model first.");
            return;
        }

        try
        {
            var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(targetPath, citadelDir);
            var gameAddonDir = Path.Combine(Directory.GetParent(citadelDir)!.Parent!.FullName, "game", container, addonName);
            var outputVpk = Path.Combine(citadelDir, $"{addonName}.vpk");

            var res = await VpkBuilder.PackAddonToVpkAsync(gameAddonDir, outputVpk);
            if (res.Success)
            {
                Log($"[Make VPK] Addon packaged successfully: {res.OutputVpkPath} ({res.FileCount} files, {res.TotalBytes / 1024 / 1024:N1} MB)");
                await DialogService.ShowInfoAsync(this, "VPK Packaged Successfully", $"Addon packaged successfully:\n\n{res.OutputVpkPath}\n\nFiles: {res.FileCount}\nSize: {res.TotalBytes / 1024 / 1024:N1} MB");
            }
            else
            {
                Log($"[Make VPK Error] {res.Message}");
                await DialogService.ShowErrorAsync(this, "VPK Packaging Failed", res.Message);
            }
        }
        catch (Exception ex)
        {
            Log($"[Make VPK Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "VPK Packaging Error", ex.Message);
        }
    }

    private async void BtnExportCsWin_Click(object? sender, RoutedEventArgs e)
    {
        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(csWinDir))
        {
            Log("[Export] CSWin64 directory or target model not configured.");
            await DialogService.ShowErrorAsync(this, "Configuration Required", "CSWin64 directory or target model is not configured.");
            return;
        }

        try
        {
            var (success, msg, filesCopied) = await VmdlPipeline.ExportToCsWinAddonAsync(
                targetPath,
                skelPath: TxtSkel.Text?.Trim(),
                graphPath: TxtGraph.Text?.Trim(),
                uiGraphPath: TxtUiGraph.Text?.Trim(),
                addSkel: ChkSkel.IsChecked == true,
                addGraph: ChkGraph.IsChecked == true,
                addUiGraph: ChkUiGraph.IsChecked == true,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir
            );

            Log(msg);
            if (success)
            {
                await DialogService.ShowInfoAsync(this, "Export Complete", msg);
            }
            else
            {
                await DialogService.ShowErrorAsync(this, "Export Failed", msg);
            }
        }
        catch (Exception ex)
        {
            Log($"[Export Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "Export Error", ex.Message);
        }
    }

    private async void BtnCompile_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var targetPath = GetResolvedTargetPath();
        var csWinDir = TxtCsWinPath.Text?.Trim();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            Log("[Compile Error] Target .vmdl file does not exist.");
            await DialogService.ShowErrorAsync(this, "Target Missing", "Target .vmdl file does not exist. Please select a valid model.");
            return;
        }

        if (!VmdlPipeline.IsValidCsWinDir(csWinDir))
        {
            Log("[Compile Error] CSWin64 compiler path is invalid or missing resourcecompiler.exe.");
            await DialogService.ShowErrorAsync(this, "Compiler Missing", "CSWin64 compiler path is invalid or missing resourcecompiler.exe.");
            return;
        }

        try
        {
            _isProcessing = true;
            BtnCompile.IsEnabled = false;
            TxtCompileBtn.Text = "compiling...";

            Log($"Starting compilation for: {Path.GetFileName(targetPath)}");

            var (success, msg) = await VmdlPipeline.ProcessVmdlFileAsync(
                targetPath,
                skelPath: TxtSkel.Text?.Trim(),
                graphPath: TxtGraph.Text?.Trim(),
                uiGraphPath: TxtUiGraph.Text?.Trim(),
                createBackup: true,
                addSkel: ChkSkel.IsChecked == true,
                addGraph: ChkGraph.IsChecked == true,
                addUiGraph: ChkUiGraph.IsChecked == true,
                upgradeHeader: true,
                compileCsWin: true,
                revertVmdl: ChkRevert.IsChecked == true,
                cswinDir: csWinDir,
                citadelAddonsDir: citadelDir
            );

            if (success)
            {
                Log($"COMPILATION SUCCESSFUL! {msg}");
                await DialogService.ShowInfoAsync(this, "Compilation Successful", $"Model compiled and deployed successfully!\n\n{msg}");
            }
            else
            {
                Log($"COMPILATION FAILED: {msg}");
                await DialogService.ShowErrorAsync(this, "Compilation Failed", $"Compilation failed:\n\n{msg}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Compile Exception] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "Compile Exception", ex.Message);
        }
        finally
        {
            _isProcessing = false;
            BtnCompile.IsEnabled = true;
            TxtCompileBtn.Text = "compile";
        }
    }
}
