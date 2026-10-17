using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

// Choosing the model to work on: addon and model lists, browse, drag and drop, and the preview.
public partial class MainWindow
{
    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        var path = DroppedPath(e);
        var usable = path != null && !_isProcessing &&
                     (Directory.Exists(path) || path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase));
        e.DragEffects = usable ? DragDropEffects.Copy : DragDropEffects.None;
        BorderDropOverlay.IsVisible = usable;
    }

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        BorderDropOverlay.IsVisible = false;
        var path = DroppedPath(e);
        if (path == null || _isProcessing) return;
        if (path.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
        {
            SelectModelFile(path);
            return;
        }
        if (!Directory.Exists(path)) return;

        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var addon = _discoveredAddons.FirstOrDefault(candidate =>
            full.Equals(Path.GetFullPath(candidate.FullPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(Path.GetFullPath(candidate.FullPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        if (addon != null)
        {
            CmbDiscovered.SelectedItem = addon;
            Log($"[drop] selected addon {addon.Name}");
            return;
        }
        // A folder outside the addons list: open its first hero model.
        var model = VmdlScanner.ScanHeroModels(path).FirstOrDefault();
        if (model != null) SelectModelFile(model.FullPath);
        else Log($"[drop] no hero model found in {path}");
    }

    private static string? DroppedPath(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).FirstOrDefault(path => path != null);

    /// <summary>Selects a model through its addon when it is listed, otherwise loads it on its own.</summary>
    private void SelectModelFile(string fullPath)
    {
        foreach (var addon in _discoveredAddons)
        {
            var listed = addon.HeroModels.FirstOrDefault(model =>
                model.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
            if (listed == null) continue;
            // Restore the addon's model list even when the addon was already selected,
            // e.g. after a manually opened file replaced it.
            _isUpdatingSelection = true;
            try
            {
                CmbDiscovered.SelectedItem = addon;
                CmbTargetVmdl.ItemsSource = addon.HeroModels;
                CmbTargetVmdl.SelectedItem = listed;
            }
            finally
            {
                _isUpdatingSelection = false;
            }
            UpdateHeroDetailsFromPath(fullPath);
            _ = Init3DSceneAsync(fullPath);
            SaveConfig();
            Log($"selected model: {addon.Name}/{Path.GetFileName(fullPath)}");
            return;
        }

        var manual = new DiscoveredModel { FullPath = fullPath, Display = Path.GetFileName(fullPath) };
        CmbTargetVmdl.ItemsSource = new List<DiscoveredModel> { manual };
        CmbTargetVmdl.SelectedIndex = 0;
        UpdateHeroDetailsFromPath(fullPath);
        _ = Init3DSceneAsync(fullPath);
        Log($"manually loaded model: {Path.GetFileName(fullPath)}");
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
                Name = $"(select addon: {_discoveredAddons.Count} available)",
                FullPath = string.Empty,
                HeroModels = new List<DiscoveredModel>(),
                Display = $"(select addon: {_discoveredAddons.Count} available)"
            };
            displayList.Add(placeholder);
            displayList.AddRange(_discoveredAddons);

            CmbDiscovered.ItemsSource = displayList;

            LblDiscoveredCount.Text = $"{_discoveredAddons.Count} addon(s) available";
            LblDiscoveredCount.Foreground = BrushMuted;

            if (logOutput)
            {
                Log($"discovered {_discoveredAddons.Count} addon(s) in: {citadelDir}");
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
            Log($"[scan error] {ex.Message}");
        }
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
        }
        SaveConfig();
    }

    private string? GetResolvedTargetPath()
    {
        if (CmbTargetVmdl.SelectedItem is DiscoveredModel selectedModel)
        {
            return selectedModel.FullPath;
        }

        return null;
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select target .vmdl file",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("valve modeldoc (*.vmdl)") { Patterns = new[] { "*.vmdl" } },
                new FilePickerFileType("all files (*.*)") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
            SelectModelFile(files[0].Path.LocalPath);
    }

    private void Rescan_Click(object? sender, RoutedEventArgs e)
    {
        RescanModels();
    }

    // -----------------------------------------------------------------
    // 3D VIEWPORT LOGIC
    // -----------------------------------------------------------------
    private async Task Init3DSceneAsync(string? targetPath = null)
    {
        var request = ++_previewRequest;
        try
        {
            var path = targetPath ?? GetResolvedTargetPath();
            var citadelDir = TxtCitadelPath.Text?.Trim();
            _ = UpdateLegacyFrameNoticeAsync(path, request);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                LblMeshName.Text = "mesh: loading preview...";
                ShowPreviewMessage("loading preview...", null);
                var mesh = await DmxModelLoader.LoadModelFromVmdlAsync(path, citadelDir);
                // A newer selection owns the viewport now.
                if (request != _previewRequest) return;
                ModelViewport.CurrentMesh = mesh;

                if (mesh != null && mesh.Vertices.Count > 0)
                {
                    LblMeshName.Text = $"mesh: {mesh.MeshName} ({mesh.Vertices.Count:N0} verts, {mesh.Indices.Count / 3:N0} tris)";
                    PanelPreviewEmpty.IsVisible = false;
                }
                else
                {
                    LblMeshName.Text = $"mesh: {Path.GetFileName(path)} (render mesh not found)";
                    ShowPreviewMessage("no render mesh found",
                        "the model's .dmx render meshes were not found next to it or in the addon.");
                }
            }
            else
            {
                ModelViewport.CurrentMesh = null;
                LblMeshName.Text = "mesh: no model selected";
                ShowPreviewMessage("no model selected",
                    "pick an addon and a target vmdl on the left, or drop a .vmdl file or an addon folder on this window.");
            }
        }
        catch (Exception ex)
        {
            if (request != _previewRequest) return;
            Log($"[preview] could not load the model: {ex.Message}");
            ShowPreviewMessage("preview failed", ex.Message);
        }
    }

    private void ShowPreviewMessage(string title, string? hint)
    {
        PanelPreviewEmpty.IsVisible = true;
        TxtPreviewEmpty.Text = title;
        TxtPreviewEmptyHint.Text = hint ?? string.Empty;
        TxtPreviewEmptyHint.IsVisible = hint != null;
    }

    private async Task UpdateLegacyFrameNoticeAsync(string? path, int request)
    {
        var legacy = false;
        try
        {
            if (!string.IsNullOrEmpty(path))
                legacy = await Task.Run(() => GameSync.UsesLegacyCameraFrameAsync(path));
        }
        catch
        {
            // An unreadable file simply gets no notice; compile and sync report the real error.
        }
        // A newer selection owns the notice now.
        if (request == _previewRequest)
            BorderLegacyFrame.IsVisible = legacy;
    }

    private void BtnResetView_Click(object? sender, RoutedEventArgs e) => ModelViewport.ResetView();
}
