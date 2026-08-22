using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

public partial class MainWindow : Window
{
    // Win11 DWM API
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38; // 2 = Mica
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33; // 2 = Rounded

    private AppConfig _config = new();
    private List<DiscoveredAddon> _discoveredAddons = new();
    private bool _isProcessing;
    private bool _isInitializing = true;
    private bool _isUpdatingSelection = false;

    // 3D Viewport State (Manual Orbit & Zoom looking towards +X)
    private bool _isDragging = false;
    private Point _lastMousePos;
    private double _yawAngle = 270.0; // Oriented looking from front (+180 deg)
    private double _pitchAngle = 8.0;
    private double _cameraDistance = 4.2;
    private const double TargetCenterY = 1.0;

    private readonly SolidColorBrush _brushOk = new(Color.FromRgb(0xDF, 0xE2, 0xE6)); // Neutral light gray (No green)
    private readonly SolidColorBrush _brushWarn = new(Color.FromRgb(0xBA, 0xBE, 0xC4));
    private readonly SolidColorBrush _brushErr = new(Color.FromRgb(0xDF, 0x70, 0x70));
    private readonly SolidColorBrush _brushMuted = new(Color.FromRgb(0x82, 0x86, 0x8E));
    private readonly SolidColorBrush _brushInfo = new(Color.FromRgb(0xDF, 0xE2, 0xE6));

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int darkMode = 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                int backdropType = 2; // Mica
                DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));

                int cornerPreference = 2; // Rounded
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            }
        }
        catch { }
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
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
            CmbTargetVmdl.Text = _config.LastTargetPath ?? string.Empty;

            ChkRevert.IsChecked = _config.ChkRevert;
            ChkSkel.IsChecked = _config.ChkSkel;
            ChkGraph.IsChecked = _config.ChkGraph;
            ChkUiGraph.IsChecked = _config.ChkUiGraph;

            // Mandatory environment validation on startup
            ValidateEnvironmentOnStartup();

            // Initialize 3D Viewport Scene looking towards +X (Async)
            _ = Init3DSceneAsync();

            RescanModels(logOutput: false);

            var resolved = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                UpdateHeroDetailsFromPath(resolved);
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
        Dispatcher.Invoke(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            TxtLog.AppendText($"[{timestamp}] {message}\n");
            if (ChkAutoScroll?.IsChecked == true)
            {
                TxtLog.ScrollToEnd();
            }
        });
    }

    // -----------------------------------------------------------------
    // 3D VIEWPORT LOGIC (Asynchronous & Cached - Zero UI Lag)
    // -----------------------------------------------------------------
    private async Task Init3DSceneAsync(string? targetPath = null)
    {
        try
        {
            var path = targetPath ?? GetResolvedTargetPath();

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblMeshName.Text = "mesh: loading preview...";
                var result = await DmxModelLoader.LoadModelFromVmdlAsync(path);
                SceneVisual.Content = result.SceneGroup;
                if (!string.IsNullOrEmpty(result.PrimaryMeshName))
                {
                    LblMeshName.Text = $"mesh: {result.PrimaryMeshName}";
                }
            }
            else
            {
                var scene = MeshBuilder3D.CreateEmptyGridScene();
                scene.Freeze();
                SceneVisual.Content = scene;
                LblMeshName.Text = "mesh: no model selected";
            }
            UpdateCamera();
        }
        catch { }
    }

    private void UpdateCamera()
    {
        try
        {
            double radYaw = _yawAngle * Math.PI / 180.0;
            double radPitch = _pitchAngle * Math.PI / 180.0;

            double x = Math.Sin(radYaw) * Math.Cos(radPitch) * _cameraDistance;
            double z = Math.Cos(radYaw) * Math.Cos(radPitch) * _cameraDistance;
            double y = TargetCenterY + Math.Sin(radPitch) * _cameraDistance;

            ViewCamera.Position = new Point3D(x, y, z);
            ViewCamera.LookDirection = new Vector3D(-x, TargetCenterY - y, -z);
        }
        catch { }
    }

    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isDragging = true;
            _lastMousePos = e.GetPosition(ModelViewport);
            ModelViewport.CaptureMouse();
        }
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging)
        {
            var pos = e.GetPosition(ModelViewport);
            double dx = pos.X - _lastMousePos.X;
            double dy = pos.Y - _lastMousePos.Y;
            _lastMousePos = pos;

            _yawAngle = (_yawAngle - dx * 0.7) % 360.0;
            _pitchAngle = Math.Clamp(_pitchAngle + dy * 0.5, -40.0, 75.0);

            UpdateCamera();
        }
    }

    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            ModelViewport.ReleaseMouseCapture();
        }
    }

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _cameraDistance = Math.Clamp(_cameraDistance - (e.Delta / 400.0), 2.0, 7.5);
        UpdateCamera();
    }

    // -----------------------------------------------------------------
    // ENVIRONMENT & MODEL DETECTION LOGIC
    // -----------------------------------------------------------------
    private void CheckEnvironmentStatus()
    {
        try
        {
            var csWinDir = TxtCsWinPath.Text.Trim();
            var isValidCsWin = VmdlPipeline.IsValidCsWinDir(csWinDir);

            if (isValidCsWin)
            {
                LblCsWinStatus.Text = "Ready (compiler found)";
                LblCsWinStatus.Foreground = _brushOk;
                if (BorderCsWinCheck != null && IconCsWinStatus != null)
                {
                    BorderCsWinCheck.Visibility = Visibility.Visible;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x1D, 0x3E, 0x2B));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    IconCsWinStatus.Data = Geometry.Parse("M9 16.2L4.8 12L3.4 13.4L9 19L21 7L19.6 5.6L9 16.2Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                    BorderCsWinCheck.ToolTip = "CSWin64 resourcecompiler.exe found and ready.";
                }
            }
            else if (string.IsNullOrEmpty(csWinDir))
            {
                LblCsWinStatus.Text = "Path not set";
                LblCsWinStatus.Foreground = _brushErr;
                if (BorderCsWinCheck != null && IconCsWinStatus != null)
                {
                    BorderCsWinCheck.Visibility = Visibility.Visible;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x1E, 0x1E));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    IconCsWinStatus.Data = Geometry.Parse("M19 6.41L17.59 5L12 10.59L6.41 5L5 6.41L10.59 12L5 17.59L6.41 19L12 13.41L17.59 19L19 17.59L13.41 12L19 6.41Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    BorderCsWinCheck.ToolTip = "Path not configured.";
                }
            }
            else
            {
                LblCsWinStatus.Text = "resourcecompiler.exe not found in this folder";
                LblCsWinStatus.Foreground = _brushErr;
                if (BorderCsWinCheck != null && IconCsWinStatus != null)
                {
                    BorderCsWinCheck.Visibility = Visibility.Visible;
                    BorderCsWinCheck.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x1E, 0x1E));
                    BorderCsWinCheck.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    IconCsWinStatus.Data = Geometry.Parse("M19 6.41L17.59 5L12 10.59L6.41 5L5 6.41L10.59 12L5 17.59L6.41 19L12 13.41L17.59 19L19 17.59L13.41 12L19 6.41Z");
                    IconCsWinStatus.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                    BorderCsWinCheck.ToolTip = "resourcecompiler.exe not found in this folder.";
                }
            }

            var citadelDir = TxtCitadelPath.Text.Trim();
            if (!string.IsNullOrEmpty(citadelDir) && Directory.Exists(citadelDir))
            {
                LblCitadelStatus.Text = "Connected to addons folder";
                LblCitadelStatus.Foreground = _brushOk;
            }
            else if (string.IsNullOrEmpty(citadelDir))
            {
                LblCitadelStatus.Text = "Not configured (optional)";
                LblCitadelStatus.Foreground = _brushMuted;
            }
            else
            {
                LblCitadelStatus.Text = "Directory not found";
                LblCitadelStatus.Foreground = _brushWarn;
            }
        }
        catch { }
    }

    private void ValidateEnvironmentOnStartup()
    {
        try
        {
            var csWin = TxtCsWinPath.Text.Trim();
            bool isCsWinValid = VmdlPipeline.IsValidCsWinDir(csWin);

            if (!isCsWinValid)
            {
                ExpanderAdvanced.IsExpanded = true;
                MessageBox.Show(
                    "Welcome to Deadlock AG2 Compiler!\n\nPlease select your CSWin64 installation folder (containing resourcecompiler.exe) to proceed.",
                    "CSWin64 Setup Required",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                var dlg = new OpenFolderDialog
                {
                    Title = "Select CSWin64 Installation Directory (containing game\\bin\\win64\\resourcecompiler.exe)"
                };

                if (dlg.ShowDialog() == true)
                {
                    var chosen = dlg.FolderName;
                    if (VmdlPipeline.IsValidCsWinDir(chosen))
                    {
                        TxtCsWinPath.Text = chosen;
                        SaveCurrentConfig();
                        Log($"[SETUP] CSWin64 path configured: {chosen}");
                    }
                    else
                    {
                        MessageBox.Show(
                            $"The selected folder is not a valid CSWin64 installation (missing resourcecompiler.exe):\n{chosen}\n\nPlease configure the path in Advanced Settings.",
                            "Invalid CSWin64 Directory",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                }
            }

            var citadel = TxtCitadelPath.Text.Trim();
            if (string.IsNullOrEmpty(citadel) || !Directory.Exists(citadel))
            {
                // Auto-detect CSDK from Deadlock or default locations
                var deadlock = DeadlockLocator.DetectDeadlockInstallation();
                if (deadlock.IsValid)
                {
                    var candContent = Path.Combine(deadlock.GameRootPath, "content", "citadel_addons");
                    if (Directory.Exists(candContent))
                    {
                        TxtCitadelPath.Text = candContent;
                        SaveCurrentConfig();
                        Log($"[SETUP] Auto-detected CSDK Addons folder: {candContent}");
                    }
                }

                if (string.IsNullOrEmpty(TxtCitadelPath.Text.Trim()) || !Directory.Exists(TxtCitadelPath.Text.Trim()))
                {
                    ExpanderAdvanced.IsExpanded = true;
                }
            }

            CheckEnvironmentStatus();
        }
        catch (Exception ex)
        {
            Log($"[SETUP ERROR] {ex.Message}");
        }
    }

    private void RescanModels(bool logOutput = true)
    {
        try
        {
            var searchDir = TxtCitadelPath.Text.Trim();
            if (string.IsNullOrEmpty(searchDir) || !Directory.Exists(searchDir))
            {
                var target = CmbTargetVmdl.Text.Trim();
                if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
                    searchDir = target;
            }

            if (string.IsNullOrEmpty(searchDir) || !Directory.Exists(searchDir))
            {
                _discoveredAddons.Clear();
                _isUpdatingSelection = true;
                CmbDiscovered.ItemsSource = new List<DiscoveredAddon>
                {
                    new DiscoveredAddon
                    {
                        Display = "(Set CSDK Addons folder to discover addons)",
                        IsPlaceholder = true
                    }
                };
                CmbDiscovered.SelectedIndex = 0;
                CmbTargetVmdl.ItemsSource = null;
                _isUpdatingSelection = false;
                LblDiscoveredCount.Text = "No addons folder selected";
                LblDiscoveredCount.Foreground = _brushMuted;
                return;
            }

            var addons = VmdlScanner.ScanAddons(searchDir);
            _discoveredAddons = addons;

            _isUpdatingSelection = true;
            if (addons.Count > 0)
            {
                var items = new List<DiscoveredAddon>();
                items.Add(new DiscoveredAddon
                {
                    Display = $"(Select addon: {addons.Count} available)",
                    IsPlaceholder = true
                });
                items.AddRange(addons);

                CmbDiscovered.ItemsSource = items;
                LblDiscoveredCount.Text = $"{addons.Count} addon(s) available";
                LblDiscoveredCount.Foreground = _brushOk;

                // Try to match previously selected target / addon
                var currentTarget = CmbTargetVmdl.Text.Trim();
                int matchedAddonIdx = -1;
                DiscoveredModel? matchedModel = null;

                if (!string.IsNullOrEmpty(currentTarget))
                {
                    for (int i = 0; i < addons.Count; i++)
                    {
                        var m = addons[i].HeroModels.FirstOrDefault(hm =>
                            string.Equals(hm.FullPath, currentTarget, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(hm.Subpath, currentTarget, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(hm.Filename, Path.GetFileName(currentTarget), StringComparison.OrdinalIgnoreCase));
                        if (m != null)
                        {
                            matchedAddonIdx = i;
                            matchedModel = m;
                            break;
                        }
                    }
                }

                if (matchedAddonIdx >= 0 && matchedModel != null)
                {
                    var selectedAddon = addons[matchedAddonIdx];
                    var subpaths = selectedAddon.HeroModels.Select(m => m.Subpath).Distinct().ToList();
                    if (!subpaths.Contains(matchedModel.Subpath, StringComparer.OrdinalIgnoreCase))
                        subpaths.Insert(0, matchedModel.Subpath);

                    CmbTargetVmdl.ItemsSource = subpaths;
                    CmbTargetVmdl.SelectedItem = matchedModel.Subpath;
                    CmbTargetVmdl.Text = matchedModel.Subpath;
                    CmbDiscovered.SelectedIndex = matchedAddonIdx + 1;
                }
                else
                {
                    CmbTargetVmdl.ItemsSource = null;
                    CmbDiscovered.SelectedIndex = 0;
                }

                if (logOutput)
                    Log($"Discovered {addons.Count} addon(s) in: {searchDir}");
            }
            else
            {
                var placeholderList = new List<DiscoveredAddon>
                {
                    new DiscoveredAddon
                    {
                        Display = "(No addons found in citadel_addons folder)",
                        IsPlaceholder = true
                    }
                };
                CmbDiscovered.ItemsSource = placeholderList;
                CmbDiscovered.SelectedIndex = 0;
                LblDiscoveredCount.Text = "0 addons found";
                LblDiscoveredCount.Foreground = _brushWarn;

                if (logOutput)
                    Log($"Scanned {searchDir}: No addons found.");
            }
            _isUpdatingSelection = false;
        }
        catch (Exception ex)
        {
            _isUpdatingSelection = false;
            Log($"[Scan Error] {ex.Message}");
        }
    }

    private void UpdateHeroDetailsFromPath(string path)
    {
        try
        {
            if (File.Exists(path) && path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
            {
                var (skel, graph, uiGraph) = VmdlPipeline.DeriveDefaultPaths(path);
                TxtCustomSkel.Text = skel;
                TxtCustomGraph.Text = graph;
                TxtCustomUiGraph.Text = uiGraph;

                var detectedHero = VmdlPipeline.DetectHeroFromPath(path);
                if (!string.IsNullOrEmpty(detectedHero))
                {
                    var items = CmbHeroPreset.ItemsSource as List<string>;
                    if (items != null)
                    {
                        var idx = items.FindIndex(k => string.Equals(k, detectedHero, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0 && CmbHeroPreset.SelectedIndex != idx)
                        {
                            _isUpdatingSelection = true;
                            CmbHeroPreset.SelectedIndex = idx;
                            _isUpdatingSelection = false;
                        }
                    }
                }

                // Update 3D Preview Information Card & load real DMX mesh with textures (Async)
                _ = Init3DSceneAsync(path);
                Log($"Loaded model: {Path.GetFileName(path)}");
            }
        }
        catch { }
    }

    private void AutoDetectAndSetCitadelDir(string filepath)
    {
        try
        {
            var detected = VmdlPipeline.ExtractCitadelAddonsDir(filepath);
            var current = TxtCitadelPath.Text.Trim();
            if (!string.IsNullOrEmpty(detected) && (string.IsNullOrEmpty(current) || !Directory.Exists(current)))
            {
                TxtCitadelPath.Text = detected;
                Log($"Auto-detected CSDK Addons folder: {detected}");
            }
        }
        catch { }
    }

    private void TxtCsWinPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        CheckEnvironmentStatus();
        SaveCurrentConfig();
    }

    private void TxtCitadelPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        CheckEnvironmentStatus();
        SaveCurrentConfig();
        RescanModels(logOutput: false);
    }

    public string GetResolvedTargetPath()
    {
        var text = CmbTargetVmdl?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // 1. Direct absolute file check
        if (Path.IsPathRooted(text) && File.Exists(text))
            return text;

        var cleanRel = text.TrimStart('/', '\\').Replace('/', '\\');

        // 2. Relative to currently selected addon
        if (CmbDiscovered?.SelectedItem is DiscoveredAddon addon && !addon.IsPlaceholder && !string.IsNullOrEmpty(addon.FullPath))
        {
            var candidate = Path.Combine(addon.FullPath, cleanRel);
            if (File.Exists(candidate))
                return candidate;

            var matchedHeroModel = addon.HeroModels.FirstOrDefault(m => string.Equals(m.Subpath, text, StringComparison.OrdinalIgnoreCase) ||
                                                                        string.Equals(m.Filename, text, StringComparison.OrdinalIgnoreCase));
            if (matchedHeroModel != null && File.Exists(matchedHeroModel.FullPath))
                return matchedHeroModel.FullPath;

            try
            {
                var files = Directory.EnumerateFiles(addon.FullPath, Path.GetFileName(text), SearchOption.AllDirectories).ToList();
                if (files.Count > 0)
                    return files[0];
            }
            catch { }

            return candidate;
        }

        // 3. Relative to configured citadel addons directory
        var globalCitadelDir = TxtCitadelPath?.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(globalCitadelDir) && Directory.Exists(globalCitadelDir))
        {
            var candidate = Path.Combine(globalCitadelDir, cleanRel);
            if (File.Exists(candidate))
                return candidate;
        }

        return text;
    }

    private void CmbTargetVmdl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingSelection) return;
        _isUpdatingSelection = true;
        try
        {
            var resolved = GetResolvedTargetPath();
            if (!string.IsNullOrEmpty(resolved))
            {
                SaveCurrentConfig();
                UpdateHeroDetailsFromPath(resolved);
            }
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void CmbTargetVmdl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingSelection) return;
        _isUpdatingSelection = true;
        try
        {
            var resolved = GetResolvedTargetPath();
            SaveCurrentConfig();
            UpdateHeroDetailsFromPath(resolved);
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void CmbDiscovered_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingSelection) return;
        _isUpdatingSelection = true;
        try
        {
            if (CmbDiscovered.SelectedItem is DiscoveredAddon addon && !addon.IsPlaceholder && !string.IsNullOrEmpty(addon.FullPath))
            {
                // 1. Populate Target VMDL combobox with hero models in this addon
                List<string> subpaths;
                if (addon.HeroModels.Count > 0)
                {
                    subpaths = addon.HeroModels.Select(m => m.Subpath).Distinct().ToList();
                }
                else
                {
                    // If no hero models detected, list any .vmdl files found in the addon
                    var citadelDir = TxtCitadelPath.Text.Trim();
                    subpaths = Directory.EnumerateFiles(addon.FullPath, "*.vmdl", SearchOption.AllDirectories)
                        .Select(p => VmdlPipeline.ParseCsdkPath(p, citadelDir).Subpath)
                        .Distinct()
                        .ToList();
                }

                CmbTargetVmdl.ItemsSource = subpaths;

                string primaryTarget = string.Empty;
                if (subpaths.Count > 0)
                {
                    primaryTarget = subpaths[0];
                    CmbTargetVmdl.SelectedItem = primaryTarget;
                    CmbTargetVmdl.Text = primaryTarget;
                }
                else
                {
                    CmbTargetVmdl.SelectedItem = null;
                    CmbTargetVmdl.Text = string.Empty;
                }

                var resolved = GetResolvedTargetPath();
                UpdateHeroDetailsFromPath(resolved);
                Log($"Selected addon: [{addon.Name}] - {subpaths.Count} target vmdl(s) available.");
            }
            else
            {
                // Nothing or placeholder selected in discovered addons -> Target dropdown must be completely empty
                CmbTargetVmdl.ItemsSource = null;
                CmbTargetVmdl.SelectedItem = null;
                CmbTargetVmdl.Text = string.Empty;
                UpdateHeroDetailsFromPath(string.Empty);
            }
        }
        catch (Exception ex)
        {
            Log($"[Selection Error] {ex.Message}");
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void CmbHeroPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingSelection) return;
        _isUpdatingSelection = true;
        try
        {
            var selected = CmbHeroPreset.SelectedItem as string;
            if (!string.IsNullOrEmpty(selected) && selected != "(Auto-Detect Hero Paths)")
            {
                var db = HeroDatabase.GetDatabase();
                if (db.TryGetValue(selected, out var preset))
                {
                    TxtCustomSkel.Text = preset.Skel;
                    TxtCustomGraph.Text = preset.Graph;
                    TxtCustomUiGraph.Text = preset.UiGraph;
                    Log($"Applied hero preset: '{selected}'");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Preset Error] {ex.Message}");
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void BrowseCsWin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select CSWin64 Installation Folder",
                InitialDirectory = Directory.Exists(TxtCsWinPath.Text.Trim()) ? TxtCsWinPath.Text.Trim() : null
            };

            if (dlg.ShowDialog() == true)
            {
                TxtCsWinPath.Text = dlg.FolderName;
            }
        }
        catch (Exception ex)
        {
            Log($"[Browse Error] {ex.Message}");
        }
    }

    private void BrowseCitadel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select CSDK citadel_addons or content Folder",
                InitialDirectory = Directory.Exists(TxtCitadelPath.Text.Trim()) ? TxtCitadelPath.Text.Trim() : null
            };

            if (dlg.ShowDialog() == true)
            {
                TxtCitadelPath.Text = dlg.FolderName;
                RescanModels(logOutput: true);
            }
        }
        catch (Exception ex)
        {
            Log($"[Browse Error] {ex.Message}");
        }
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var initialDir = Directory.Exists(TxtCitadelPath.Text.Trim()) ? TxtCitadelPath.Text.Trim() : null;
            var dlg = new OpenFileDialog
            {
                Title = "Select VMDL Model File",
                Filter = "VMDL Model Files (*.vmdl)|*.vmdl|All Files (*.*)|*.*",
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() == true)
            {
                var chosen = dlg.FileName;
                var citadelDir = TxtCitadelPath.Text.Trim();
                var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(chosen, citadelDir);

                var targetSubpath = !string.IsNullOrEmpty(subpath) && subpath != "addon" ? subpath : chosen;
                var currentList = (CmbTargetVmdl.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
                if (!currentList.Contains(targetSubpath, StringComparer.OrdinalIgnoreCase))
                    currentList.Insert(0, targetSubpath);

                _isUpdatingSelection = true;
                CmbTargetVmdl.ItemsSource = currentList;
                CmbTargetVmdl.SelectedItem = targetSubpath;
                _isUpdatingSelection = false;

                AutoDetectAndSetCitadelDir(chosen);
                UpdateHeroDetailsFromPath(chosen);
            }
        }
        catch (Exception ex)
        {
            Log($"[Browse Error] {ex.Message}");
        }
    }

    private void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanModels(logOutput: true);
    }

    private void OptionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SaveCurrentConfig();
    }

    private void SaveCurrentConfig()
    {
        try
        {
            if (_isInitializing) return;

            _config.CsWinDir = TxtCsWinPath.Text.Trim();
            _config.CitadelAddonsDir = TxtCitadelPath.Text.Trim();
            _config.LastTargetPath = CmbTargetVmdl.Text.Trim();
            _config.ChkRevert = ChkRevert.IsChecked == true;
            _config.ChkSkel = ChkSkel.IsChecked == true;
            _config.ChkGraph = ChkGraph.IsChecked == true;
            _config.ChkUiGraph = ChkUiGraph.IsChecked == true;

            ConfigManager.SaveConfig(_config);
        }
        catch { }
    }

    private async void BtnCompile_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var path = GetResolvedTargetPath();
        var cswinDir = TxtCsWinPath.Text.Trim();
        var citadelDir = TxtCitadelPath.Text.Trim();

        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("Please select a .vmdl file first.", "No Target Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show($"Target file does not exist:\n{path}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Target file must be a .vmdl model file.", "Invalid File Type", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (!Directory.Exists(cswinDir))
        {
            MessageBox.Show($"CSWin64 directory does not exist:\n{cswinDir}", "CSWin64 Path Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var targets = new List<string> { path };

        _isProcessing = true;
        BtnCompile.IsEnabled = false;
        TxtCompileBtn.Text = "compiling...";

        Log($"Starting compilation for: {Path.GetFileName(path)}");

        var skelCustom = !string.IsNullOrWhiteSpace(TxtCustomSkel.Text) ? TxtCustomSkel.Text.Trim() : null;
        var graphCustom = !string.IsNullOrWhiteSpace(TxtCustomGraph.Text) ? TxtCustomGraph.Text.Trim() : null;
        var uiGraphCustom = !string.IsNullOrWhiteSpace(TxtCustomUiGraph.Text) ? TxtCustomUiGraph.Text.Trim() : null;

        var chkSkel = ChkSkel.IsChecked == true;
        var chkGraph = ChkGraph.IsChecked == true;
        var chkUiGraph = ChkUiGraph.IsChecked == true;
        var chkRevert = ChkRevert.IsChecked == true;

        int updatedCount = 0;
        int errorCount = 0;

        await Task.Run(async () =>
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i];

                try
                {
                    var (success, msg) = await VmdlPipeline.ProcessVmdlFileAsync(
                        target,
                        skelPath: skelCustom,
                        graphPath: graphCustom,
                        uiGraphPath: uiGraphCustom,
                        createBackup: false,
                        addSkel: chkSkel,
                        addGraph: chkGraph,
                        addUiGraph: chkUiGraph,
                        upgradeHeader: true,
                        compileCsWin: true,
                        revertVmdl: chkRevert,
                        cswinDir: cswinDir,
                        citadelAddonsDir: citadelDir
                    );

                    Dispatcher.Invoke(() =>
                    {
                        if (success)
                        {
                            updatedCount++;
                            Log($"[SUCCESS] {Path.GetFileName(target)}: {msg}");
                        }
                        else
                        {
                            errorCount++;
                            Log($"[FAILED] {Path.GetFileName(target)}: {msg}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        errorCount++;
                        Log($"[EXCEPTION] {Path.GetFileName(target)}: {ex.Message}");
                    });
                }
            }
        });

        var summary = $"Compilation finished. Processed: {updatedCount}, Errors: {errorCount}";
        Log(summary);

        _isProcessing = false;
        BtnCompile.IsEnabled = true;
        TxtCompileBtn.Text = "compile";

        if (errorCount == 0)
        {
            MessageBox.Show(summary, "Compilation Complete", MessageBoxButton.OK, MessageBoxImage.Information);

            // Post-compile VPK packaging prompt
            if (_config.PromptVpkAfterCompile)
            {
                var askVpk = MessageBox.Show(
                    "Compilation was successful!\n\nDo you want to package the compiled addon into a .vpk file now?",
                    "Make VPK Package",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (askVpk == MessageBoxResult.Yes)
                {
                    _ = PromptAndBuildVpkAsync();
                }
            }
        }
        else
        {
            MessageBox.Show(summary, "Compilation Completed with Warnings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnExportCsWin_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var path = GetResolvedTargetPath();
        var cswinDir = TxtCsWinPath.Text.Trim();
        var citadelDir = TxtCitadelPath.Text.Trim();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("Please select an existing .vmdl file first.", "No Target Model", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(cswinDir) || !Directory.Exists(cswinDir))
        {
            MessageBox.Show("Please set a valid CSWin64 installation path first.", "CSWin64 Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isProcessing = true;
        BtnExportCsWin.IsEnabled = false;
        TxtExportCsWinBtn.Text = "exporting...";

        Log($"[EXPORT] Exporting model & assets with AG2 nodes to CSWin64...");

        var skelCustom = !string.IsNullOrWhiteSpace(TxtCustomSkel.Text) ? TxtCustomSkel.Text.Trim() : null;
        var graphCustom = !string.IsNullOrWhiteSpace(TxtCustomGraph.Text) ? TxtCustomGraph.Text.Trim() : null;
        var uiGraphCustom = !string.IsNullOrWhiteSpace(TxtCustomUiGraph.Text) ? TxtCustomUiGraph.Text.Trim() : null;

        var chkSkel = ChkSkel.IsChecked == true;
        var chkGraph = ChkGraph.IsChecked == true;
        var chkUiGraph = ChkUiGraph.IsChecked == true;

        var (success, msg, count) = await Task.Run(() => VmdlPipeline.ExportToCsWinAddonAsync(
            path,
            skelPath: skelCustom,
            graphPath: graphCustom,
            uiGraphPath: uiGraphCustom,
            addSkel: chkSkel,
            addGraph: chkGraph,
            addUiGraph: chkUiGraph,
            cswinDir: cswinDir,
            citadelAddonsDir: citadelDir
        ));

        _isProcessing = false;
        BtnExportCsWin.IsEnabled = true;
        TxtExportCsWinBtn.Text = "export to cswin64";

        if (success)
        {
            Log($"[EXPORT SUCCESS] {msg}");
            MessageBox.Show($"Successfully exported {count} asset file(s) to CSWin64 addon with injected AG2 nodes!\n\nYou can now open and edit this model with full AnimGraph2 support in CSWin64 ModelDoc.", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            Log($"[EXPORT FAILED] {msg}");
            MessageBox.Show($"Failed to export model to CSWin64:\n{msg}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnSanitizeModelDoc_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var path = GetResolvedTargetPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show("Please select an existing .vmdl file first.", "No Target Model", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isProcessing = true;
        BtnSanitizeModelDoc.IsEnabled = false;
        TxtSanitizeBtn.Text = "fixing...";

        Log($"[MODELDOC FIX] Sanitizing {Path.GetFileName(path)} for CSDK12 ModelDoc compatibility...");

        var (success, msg) = await Task.Run(() => VmdlPipeline.SanitizeVmdlForModelDocAsync(path, createBackup: true));

        _isProcessing = false;
        BtnSanitizeModelDoc.IsEnabled = true;
        TxtSanitizeBtn.Text = "fix for modeldoc";

        if (success)
        {
            Log($"[MODELDOC FIX SUCCESS] {msg}");
            MessageBox.Show($"Successfully cleaned {Path.GetFileName(path)} for CSDK12 ModelDoc!\n\n• Stripped NmSkeletonList & AnimGraph2List nodes\n• Set disabled = true on AnimationList (keeping clips intact)\n• Created .bak backup file\n\nYou can now open this model in CSDK12 ModelDoc without crashes.", "ModelDoc Fix Applied", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateHeroDetailsFromPath(path);
        }
        else
        {
            Log($"[MODELDOC FIX FAILED] {msg}");
            MessageBox.Show($"Failed to sanitize VMDL:\n{msg}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnTransferCloth_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        var path = GetResolvedTargetPath();
        var citadelDir = TxtCitadelPath.Text.Trim();

        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show("Please select a target .vmdl file first.", "No Target Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!File.Exists(path))
        {
            MessageBox.Show($"Target file does not exist:\n{path}", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirm = MessageBox.Show(
            "Transfer Cloth will automatically extract and configure:\n\n" +
            "• Softbody Dynamic Chains (hair, braids, tails, props, chains, shackles, sleeves)\n" +
            "• Body Collision Spheres (ClothShapeSphere colliders for head, torso, pelvis)\n\n" +
            "⚠️ Notice:\n" +
            "• Cloth Proxy Meshes (.dmx 2D meshes for coats/skirts/dresses) CANNOT be automatically extracted from compiled game files and must be created manually (e.g., in Blender).\n" +
            "• If you place your custom .dmx cloth proxy in the model folder, it will be automatically linked with authentic physics settings.\n" +
            "• This feature is designed exclusively for vanilla hero skeleton setups.\n\n" +
            "Do you want to proceed with the cloth transfer?",
            "Transfer Cloth Simulation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (confirm != MessageBoxResult.Yes)
            return;

        _isProcessing = true;
        BtnTransferCloth.IsEnabled = false;
        TxtTransferClothBtn.Text = "extracting...";
        Log($"[CLOTH TRANSFER] Extracting authentic cloth chains & colliders for {Path.GetFileName(path)}...");

        var (success, msg, count) = await Task.Run(() => ClothPhysicsExtractor.TransferClothPhysics(path, citadelDir));

        _isProcessing = false;
        BtnTransferCloth.IsEnabled = true;
        TxtTransferClothBtn.Text = "transfer cloth";

        if (success)
        {
            Log($"[CLOTH TRANSFER SUCCESS] {msg.Replace("\n", " | ")}");
            MessageBox.Show(msg, "Cloth Physics Transferred", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateHeroDetailsFromPath(path);
        }
        else
        {
            Log($"[CLOTH TRANSFER FAILED] {msg}");
            MessageBox.Show(msg, "Transfer Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void BtnUpdateVpkPresets_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;

        try
        {
            string? targetVpkPath = null;

            // 1. Try automatic detection from Steam libraries / registry
            var detected = DeadlockLocator.DetectDeadlockInstallation();
            if (detected.IsValid)
            {
                var ask = MessageBox.Show(
                    $"Auto-detected Deadlock installation at:\n{detected.GameRootPath}\n\nDo you want to scan hero presets from this game folder?",
                    "Deadlock Auto-Detected",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (ask == MessageBoxResult.Cancel) return;

                if (ask == MessageBoxResult.Yes)
                {
                    targetVpkPath = detected.Pak01VpkPath;
                }
            }

            // 2. If not auto-detected or user chose No, let user browse for the Deadlock game folder
            if (string.IsNullOrEmpty(targetVpkPath))
            {
                var dlg = new OpenFolderDialog
                {
                    Title = "Select Deadlock Game Folder (containing game\\bin\\win64\\deadlock.exe)",
                    InitialDirectory = Directory.Exists(TxtCitadelPath.Text.Trim()) ? TxtCitadelPath.Text.Trim() : null
                };

                if (dlg.ShowDialog() != true) return;

                var chosenDir = dlg.FolderName;
                var info = DeadlockLocator.ValidateAndExtractInfo(chosenDir);

                if (!info.IsValid)
                {
                    MessageBox.Show(
                        $"Selected folder is not a valid Deadlock game directory!\n\nCould not find:\n• 'game\\bin\\win64\\deadlock.exe'\n• 'game\\citadel\\pak01_dir.vpk'\n\nPlease select the main Deadlock folder (e.g. steamapps\\common\\Deadlock).",
                        "Invalid Game Directory",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                targetVpkPath = info.Pak01VpkPath;
            }

            _isProcessing = true;
            BtnUpdateVpkPresets.IsEnabled = false;
            TxtGetListsBtn.Text = "getting lists...";

            Log($"[GAME SCAN] Scanning {Path.GetFileName(targetVpkPath)} for AG2 hero models in heroes_staging & heroes_wip...");

            var (success, msg, presets) = await VpkHeroScanner.ScanVpkForHeroesAsync(targetVpkPath);

            _isProcessing = false;
            BtnUpdateVpkPresets.IsEnabled = true;
            TxtGetListsBtn.Text = "get ag2 lists";

            if (success)
            {
                Log($"[GAME SCAN SUCCESS] {msg}");

                // Refresh Preset ComboBox
                var presetItems = new List<string> { "(Auto-Detect Hero Paths)" };
                presetItems.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
                CmbHeroPreset.ItemsSource = presetItems;
                CmbHeroPreset.SelectedIndex = 0;

                // Re-derive for currently loaded model if any
                var curPath = CmbTargetVmdl.Text.Trim();
                if (!string.IsNullOrEmpty(curPath) && File.Exists(curPath))
                {
                    UpdateHeroDetailsFromPath(curPath);
                }

                MessageBox.Show(msg, "Hero Presets Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"[GAME SCAN FAILED] {msg}");
                MessageBox.Show($"Failed to scan Deadlock game for hero presets:\n{msg}", "Game Scan Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            BtnUpdateVpkPresets.IsEnabled = true;
            BtnUpdateVpkPresets.Content = "Get AG2 Lists";
            Log($"[GAME SCAN ERROR] {ex.Message}");
            MessageBox.Show($"Error scanning Deadlock game:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnRestorePresets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var res = MessageBox.Show(
                "Are you sure you want to restore the original factory hero presets?\nThis will reset hero_paths.json back to its default clean state.",
                "Restore Original List",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            var (success, msg, count) = HeroDatabase.RestoreOriginalDatabase();
            if (success)
            {
                // Refresh Preset ComboBox
                var presetItems = new List<string> { "(Auto-Detect Hero Paths)" };
                presetItems.AddRange(HeroDatabase.GetDatabase().Keys.OrderBy(k => k));
                CmbHeroPreset.ItemsSource = presetItems;
                CmbHeroPreset.SelectedIndex = 0;

                // Re-derive for currently loaded model if any
                var curPath = CmbTargetVmdl.Text.Trim();
                if (!string.IsNullOrEmpty(curPath) && File.Exists(curPath))
                {
                    UpdateHeroDetailsFromPath(curPath);
                }

                Log($"[PRESETS] {msg}");
                MessageBox.Show(msg, "Original Presets Restored", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"[PRESETS ERROR] {msg}");
                MessageBox.Show(msg, "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            Log($"[PRESETS ERROR] {ex.Message}");
            MessageBox.Show($"Error restoring presets:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnMakeVpk_Click(object sender, RoutedEventArgs e)
    {
        _ = PromptAndBuildVpkAsync();
    }

    private async Task PromptAndBuildVpkAsync()
    {
        if (_isProcessing) return;

        try
        {
            var path = GetResolvedTargetPath();
            var citadelDir = TxtCitadelPath.Text.Trim();

            // Resolve target addon name and its compiled game directory
            var (container, addonName, subpath) = VmdlPipeline.ParseCsdkPath(path, citadelDir);
            if (string.IsNullOrEmpty(addonName))
            {
                MessageBox.Show("Please select a valid model or addon first.", "No Addon Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Determine source game addon folder: <root>/game/<container>/<addonName>
            string? sourceGameDir = null;
            var cleanPath = path.Replace('\\', '/');

            if (cleanPath.Contains("/content/", StringComparison.OrdinalIgnoreCase))
            {
                var idx = cleanPath.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
                var root = cleanPath[..idx];
                sourceGameDir = Path.Combine(root, "game", container, addonName);
            }
            else if (!string.IsNullOrWhiteSpace(citadelDir) && citadelDir.Replace('\\', '/').Contains("/content/", StringComparison.OrdinalIgnoreCase))
            {
                var cleanCitadel = citadelDir.Replace('\\', '/');
                var idx = cleanCitadel.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
                var root = cleanCitadel[..idx];
                sourceGameDir = Path.Combine(root, "game", container, addonName);
            }
            else if (!string.IsNullOrWhiteSpace(citadelDir))
            {
                sourceGameDir = Path.Combine(citadelDir, addonName);
            }

            if (string.IsNullOrEmpty(sourceGameDir) || !Directory.Exists(sourceGameDir))
            {
                MessageBox.Show($"Compiled game directory for addon '{addonName}' was not found at:\n{sourceGameDir}\n\nPlease compile the model first.", "Addon Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Prompt user for output .vpk destination
            var defaultDir = !string.IsNullOrEmpty(_config.LastVpkExportDir) && Directory.Exists(_config.LastVpkExportDir)
                ? _config.LastVpkExportDir
                : (Directory.GetParent(sourceGameDir)?.FullName ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop));

            var saveDlg = new SaveFileDialog
            {
                Title = $"Save {addonName}.vpk Package",
                Filter = "Source 2 VPK Archive (*.vpk)|*.vpk|All Files (*.*)|*.*",
                FileName = $"{addonName}.vpk",
                InitialDirectory = defaultDir
            };

            if (saveDlg.ShowDialog() != true) return;

            var chosenVpkPath = saveDlg.FileName;
            _config.LastVpkExportDir = Path.GetDirectoryName(chosenVpkPath);
            ConfigManager.SaveConfig(_config);

            _isProcessing = true;
            BtnMakeVpk.IsEnabled = false;
            TxtMakeVpkBtn.Text = "packing vpk...";

            Log($"[VPK] Packaging addon '{addonName}' from: {sourceGameDir} -> {chosenVpkPath}");

            var packRes = await VpkBuilder.PackAddonToVpkAsync(sourceGameDir, chosenVpkPath);

            _isProcessing = false;
            BtnMakeVpk.IsEnabled = true;
            TxtMakeVpkBtn.Text = "make vpk...";

            if (packRes.Success)
            {
                Log($"[VPK SUCCESS] {packRes.Message}");
                MessageBox.Show(packRes.Message, "VPK Created Successfully", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                Log($"[VPK FAILED] {packRes.Message}");
                MessageBox.Show(packRes.Message, "VPK Creation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _isProcessing = false;
            BtnMakeVpk.IsEnabled = true;
            TxtMakeVpkBtn.Text = "Make VPK...";
            Log($"[VPK ERROR] {ex.Message}");
            MessageBox.Show($"Error creating VPK:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
    }
}
