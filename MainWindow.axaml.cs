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

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _isInitializing = true;
            _isUpdatingSelection = true;
            DmxModelLoader.DebugLogger = Log;

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

            // First-time setup wizard
            var csValid = VmdlPipeline.IsValidCsWinDir(TxtCsWinPath.Text?.Trim());
            var citValid = !string.IsNullOrWhiteSpace(TxtCitadelPath.Text?.Trim()) && Directory.Exists(TxtCitadelPath.Text?.Trim());

            if (!csValid || !citValid)
            {
                await PromptFirstTimeSetupAsync(!csValid, !citValid);
            }
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

    private void BtnToggleAdvanced_Click(object? sender, RoutedEventArgs e)
    {
        PanelAdvancedContent.IsVisible = !PanelAdvancedContent.IsVisible;
        IconAdvancedChevron.Data = PanelAdvancedContent.IsVisible
            ? Geometry.Parse("M7.41 15.41L12 10.83L16.59 15.41L18 14L12 8L6 14L7.41 15.41Z")
            : Geometry.Parse("M7.41 8.59L12 13.17L16.59 8.59L18 10L12 16L6 10L7.41 8.59Z");
    }

    private async Task PromptFirstTimeSetupAsync(bool needCsWin, bool needCitadel)
    {
        await DialogService.ShowInfoAsync(
            this,
            "Initial Setup",
            "Welcome! Please configure your CSWin64 compiler and Citadel Addons directory to get started."
        );

        if (needCsWin)
        {
            var csFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select CSWin64 Directory (containing resourcecompiler.exe)"
            });

            if (csFolders.Count > 0)
            {
                TxtCsWinPath.Text = csFolders[0].Path.LocalPath;
                Log($"[SETUP] CSWin64 path configured: {csFolders[0].Path.LocalPath}");
                SaveConfig();
            }
        }

        if (needCitadel)
        {
            var citFolders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Citadel Addons Directory (content/citadel_addons)"
            });

            if (citFolders.Count > 0)
            {
                TxtCitadelPath.Text = citFolders[0].Path.LocalPath;
                Log($"[SETUP] Citadel addons path configured: {citFolders[0].Path.LocalPath}");
                SaveConfig();
                RescanModels();
            }
        }

        CheckEnvironmentStatus();
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
            var citadelDir = TxtCitadelPath.Text?.Trim();

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblMeshName.Text = "mesh: loading preview...";
                var mesh = await DmxModelLoader.LoadModelFromVmdlAsync(path, citadelDir);
                ModelViewport.CurrentMesh = mesh;

                if (mesh != null && mesh.Vertices.Count > 0)
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
            await DialogService.ShowInfoAsync(this, "Presets Restored", "Default hero presets restored successfully.");
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
            Log("Locating Deadlock VPK files for updated hero paths...");

            string? vpkPath = null;

            // 1. Check auto-detected install
            var deadlockInfo = DeadlockLocator.DetectDeadlockInstallation();
            if (deadlockInfo.IsValid && File.Exists(deadlockInfo.Pak01VpkPath))
            {
                vpkPath = deadlockInfo.Pak01VpkPath;
            }

            // 2. Check relative to citadel addons path
            if (string.IsNullOrEmpty(vpkPath))
            {
                var cit = TxtCitadelPath.Text?.Trim();
                if (!string.IsNullOrEmpty(cit))
                {
                    var cands = new[]
                    {
                        Path.Combine(cit, "..", "..", "game", "citadel", "pak01_dir.vpk"),
                        Path.Combine(cit, "..", "game", "citadel", "pak01_dir.vpk"),
                        Path.Combine(cit, "pak01_dir.vpk")
                    };

                    foreach (var c in cands)
                    {
                        var full = Path.GetFullPath(c);
                        if (File.Exists(full)) { vpkPath = full; break; }
                    }
                }
            }

            // 3. If still not found, prompt user to select pak01_dir.vpk
            if (string.IsNullOrEmpty(vpkPath))
            {
                var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Deadlock pak01_dir.vpk to scan hero presets",
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Deadlock VPK (*.vpk)") { Patterns = new[] { "pak01_dir.vpk", "*.vpk" } }
                    }
                });

                if (files.Count > 0)
                {
                    vpkPath = files[0].Path.LocalPath;
                }
            }

            Log($"Scanning VPK: {vpkPath}...");
            
            PanelScanProgress.IsVisible = true;
            PrgScanVpk.Value = 0;
            LblScanStatus.Text = "Starting VPK scan...";
            TxtGetListsBtn.Text = "scanning...";

            var progress = new Progress<(int Current, int Total, string CurrentModel)>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (p.Total > 0)
                    {
                        PrgScanVpk.Maximum = p.Total;
                        PrgScanVpk.Value = p.Current;
                        LblScanStatus.Text = $"scanning ({p.Current}/{p.Total}): {p.CurrentModel}";
                        TxtGetListsBtn.Text = $"{p.Current}/{p.Total}";
                    }
                });
            });

            var (success, msg, presets) = await VpkHeroScanner.ScanVpkForHeroesAsync(vpkPath, progress);
            
            PanelScanProgress.IsVisible = false;
            TxtGetListsBtn.Text = "get ag2 lists";

            if (success && presets.Count > 0)
            {
                Log($"VPK Scan Complete: Updated {presets.Count} hero presets.");
                var list = new List<string> { "(Auto-Detect Hero Paths)" };
                list.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
                CmbHeroPreset.ItemsSource = list;
                CmbHeroPreset.SelectedIndex = 0;
                await DialogService.ShowInfoAsync(this, "VPK Presets Updated", $"Hero presets updated successfully ({presets.Count} heroes).");
            }
            else
            {
                Log($"[VPK Scan Notice] {msg}");
                await DialogService.ShowErrorAsync(this, "VPK Scan Notice", msg);
            }
        }
        catch (Exception ex)
        {
            PanelScanProgress.IsVisible = false;
            TxtGetListsBtn.Text = "get ag2 lists";
            Log($"[VPK Scan Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "VPK Scan Error", ex.Message);
        }
        finally
        {
            _isProcessing = false;
            PanelScanProgress.IsVisible = false;
            TxtGetListsBtn.Text = "get ag2 lists";
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
                await DialogService.ShowInfoAsync(this, "ModelDoc Fixed", "ModelDoc syntax cleaned successfully.");
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

    private async Task<bool> MakeVpkAsync(bool suppressSuccessDialog = false)
    {
        var targetPath = GetResolvedTargetPath();
        var citadelDir = TxtCitadelPath.Text?.Trim();

        if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(citadelDir))
        {
            Log("[Make VPK] Please select an addon and target model first.");
            await DialogService.ShowErrorAsync(this, "Selection Required", "Please select an addon and target model first.");
            return false;
        }

        try
        {
            var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(targetPath, citadelDir);
            var gameAddonDir = VmdlPipeline.ResolveGameAddonDir(targetPath, citadelDir, addonName);

            // If game directory does not exist, fallback to content addon directory
            if (!Directory.Exists(gameAddonDir))
            {
                var contentAddonDir = Path.Combine(citadelDir, addonName);
                if (Directory.Exists(contentAddonDir))
                {
                    gameAddonDir = contentAddonDir;
                }
                else
                {
                    await DialogService.ShowErrorAsync(this, "VPK Packaging Failed", $"Source directory does not exist:\n{gameAddonDir}");
                    return false;
                }
            }

            // Let user choose destination path & filename
            var saveFile = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Addon VPK File",
                DefaultExtension = "vpk",
                SuggestedFileName = $"{addonName}.vpk",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Valve Pack File (*.vpk)") { Patterns = new[] { "*.vpk" } }
                }
            });

            if (saveFile == null)
            {
                Log("[Make VPK] Packaging cancelled by user.");
                return false;
            }

            var outputVpk = saveFile.Path.LocalPath;

            var res = await VpkBuilder.PackAddonToVpkAsync(gameAddonDir, outputVpk);
            if (res.Success)
            {
                Log($"[Make VPK] Addon packaged successfully: {res.OutputVpkPath} ({res.FileCount} files, {res.TotalBytes / 1024 / 1024:N1} MB)");
                if (!suppressSuccessDialog)
                {
                    await DialogService.ShowInfoAsync(this, "VPK Created", "Addon packaged into VPK successfully.");
                }
                return true;
            }
            else
            {
                Log($"[Make VPK Error] {res.Message}");
                await DialogService.ShowErrorAsync(this, "VPK Packaging Failed", res.Message);
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"[Make VPK Error] {ex.Message}");
            await DialogService.ShowErrorAsync(this, "VPK Packaging Error", ex.Message);
            return false;
        }
    }

    private async void BtnMakeVpk_Click(object? sender, RoutedEventArgs e)
    {
        await MakeVpkAsync(suppressSuccessDialog: false);
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
                await DialogService.ShowInfoAsync(this, "Export Complete", "Exported to CSWin64 successfully.");
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

                var packVpk = await DialogService.ShowConfirmAsync(
                    this,
                    "Compilation Successful",
                    "Model compiled and deployed successfully!\n\nWould you like to package the addon into a .vpk archive now?"
                );

                if (packVpk)
                {
                    await MakeVpkAsync(suppressSuccessDialog: false);
                }
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
