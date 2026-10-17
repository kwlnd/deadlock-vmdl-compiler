using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler;

// Hero and NPC presets: lists, portraits, custom preset files and detection from the model path.
public partial class MainWindow
{
    private void PopulateHeroPresets()
    {
        var previousKey = (CmbHeroPreset.SelectedItem as HeroPresetChoice)?.Key;
        var choices = HeroDatabase.GetVisiblePresets()
            .Where(pair => !pair.Value.NamedGraphs.ContainsKey("Neutrals"))
            .Select(pair => new HeroPresetChoice(pair.Key,
                HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value),
                detail: null))
            .OrderBy(choice => choice.Hero == null ? 1 : 0)
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        choices.Insert(0, new HeroPresetChoice(string.Empty, isAuto: true));
        foreach (var choice in choices)
        {
            if (choice.Hero != null && _presetPortraits.TryGetValue(choice.Hero.HeroKey, out var portrait))
                choice.Portrait = portrait;
        }
        _presetChoices = choices;
        CmbHeroPreset.ItemsSource = choices;
        CmbHeroPreset.SelectedItem = choices.FirstOrDefault(choice => choice.Key == previousKey) ?? choices[0];
        var source = HeroDatabase.ActiveCustomFilePath;
        LblHeroPathsSource.Text = $"{(source == null ? "built-in list" : Path.GetFileName(source))} · {choices.Count - 1} presets";
        ToolTip.SetTip(LblHeroPathsSource, source ?? HeroDatabase.CustomLoadError ?? "Presets bundled inside this EXE.");
        BtnDefaultHeroPaths.IsVisible = source != null || !string.IsNullOrWhiteSpace(_config.HeroPathsFile);
    }

    private void PopulateNpcPresets(IReadOnlyList<NpcPreset> scannedPresets)
    {
        var previousKey = (CmbNpcPreset.SelectedItem as NpcPresetChoice)?.Key;
        var choices = new List<NpcPresetChoice>
        {
            new NpcPresetChoice(string.Empty, "auto-detect NPC paths", "from selected model", isAuto: true)
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, preset) in HeroDatabase.GetVisiblePresets()
                     .Where(pair => pair.Value.NamedGraphs.ContainsKey("Neutrals"))
                     .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var signature = PresetSignature(preset);
            if (!seen.Add(signature)) continue;
            choices.Add(new NpcPresetChoice(
                key,
                key,
                "neutral AG2 preset",
                preset));
        }

        foreach (var npc in scannedPresets.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var signature = PresetSignature(npc.Preset);
            if (npc.IsNeutral && seen.Contains(signature)) continue;
            choices.Add(new NpcPresetChoice(
                npc.Key,
                npc.DisplayName,
                npc.IsNeutral ? "neutral model from pak01_dir.vpk" : npc.SourcePath,
                npc.Preset,
                sourcePath: npc.SourcePath));
            if (npc.IsNeutral) seen.Add(signature);
        }

        _npcPresetChoices = choices;
        CmbNpcPreset.ItemsSource = choices;
        CmbNpcPreset.SelectedItem = choices.FirstOrDefault(choice =>
            choice.Key.Equals(previousKey, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        LblNpcPresetStatus.Text = $"{Math.Max(0, choices.Count - 1)} NPC/neutral presets";
    }

    private static string PresetSignature(HeroPreset preset) =>
        string.Join("\n", new[] { preset.Skel, preset.Graph, preset.UiGraph }
            .Concat(preset.NamedGraphs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)));

    private void BtnDefaultHeroPaths_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        _config.HeroPathsFile = null;
        _config.UseBuiltInHeroPaths = true;
        HeroDatabase.UseBuiltInDatabase();
        SaveConfig();
        PopulateHeroPresets();
        Log("[ag2 presets] using the built-in preset list.");
    }

    private async void BtnLoadHeroPaths_Click(object? sender, RoutedEventArgs e)
    {
        if (_isProcessing) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select ag2 preset list (hero_paths.json format)",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ag2 preset list (*.json)") { Patterns = new[] { "*.json" } }
            }
        });
        if (files.Count == 0) return;

        var path = files[0].Path.LocalPath;
        try
        {
            var count = HeroDatabase.LoadCustomDatabase(path);
            _config.HeroPathsFile = path;
            _config.UseBuiltInHeroPaths = false;
            SaveConfig();
            PopulateHeroPresets();
            Log($"[ag2 presets] loaded {count} preset(s) from {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            // The active list is untouched when a file is rejected.
            Log($"[ag2 presets] {Path.GetFileName(path)} rejected: {ex.Message}");
            await DialogService.ShowErrorAsync(this, "preset list rejected", ex.Message);
        }
    }

    private async Task LoadNpcPresetsAsync(string vpkPath)
    {
        try
        {
            LblNpcPresetStatus.Text = "scanning pak01_dir.vpk...";
            var result = await VpkHeroScanner.ScanVpkForNpcsAsync(vpkPath);
            if (!IsVisible) return;

            PopulateNpcPresets(result.Presets);
            var currentTarget = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(currentTarget))
                UpdateHeroDetailsFromPath(currentTarget);
            Log($"[npc presets] {result.Message}");
        }
        catch (Exception ex)
        {
            LblNpcPresetStatus.Text = "NPC scan failed";
            Log($"[npc presets] scan failed: {ex.Message}");
        }
    }

    private async Task LoadPresetPortraitsAsync(string vpkPath)
    {
        if (!File.Exists(vpkPath)) return;
        await _portraitLoadGate.WaitAsync();
        try
        {
            if (_portraitSource?.Equals(vpkPath, StringComparison.OrdinalIgnoreCase) == true &&
                _presetPortraits.Count == DeadlockHeroCatalog.GetHeroes().Count(hero => hero.IconVpkPath != null))
                return;

            var pngs = await Task.Run(() => HeroIconLoader.LoadSmallPortraits(
                vpkPath, DeadlockHeroCatalog.GetHeroes()));
            if (!IsVisible) return;

            var portraits = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (var (heroKey, png) in pngs)
            {
                try
                {
                    using var input = new MemoryStream(png);
                    portraits[heroKey] = new Bitmap(input);
                }
                catch (Exception ex)
                {
                    Log($"[ag2 presets] could not display portrait for {heroKey}: {ex.Message}");
                }
            }
            foreach (var choice in _presetChoices)
                choice.Portrait = choice.Hero != null &&
                                  portraits.TryGetValue(choice.Hero.HeroKey, out var portrait)
                    ? portrait : null;
            foreach (var oldPortrait in _presetPortraits.Values)
                oldPortrait.Dispose();
            _presetPortraits = portraits;
            _portraitSource = vpkPath;
            var available = DeadlockHeroCatalog.GetHeroes().Count(hero => hero.IconVpkPath != null);
            if (portraits.Count != available)
                Log($"[ag2 presets] loaded {portraits.Count} of {available} hero portraits from VPK.");
        }
        catch (Exception ex)
        {
            Log($"[ag2 presets] hero portraits unavailable: {ex.Message}");
        }
        finally
        {
            _portraitLoadGate.Release();
        }
    }

    private void ApplyPresetPaths(HeroPreset? preset)
    {
        _isApplyingPreset = true;
        try
        {
            TxtSkel.Text = preset?.Skel ?? string.Empty;
            TxtGraph.Text = preset?.Graph ?? string.Empty;
            TxtUiGraph.Text = preset?.UiGraph ?? string.Empty;
            _selectedNamedGraphs = preset?.NamedGraphs is { Count: > 0 } graphs
                ? new Dictionary<string, string>(graphs, StringComparer.OrdinalIgnoreCase) : null;
            LblNamedGraphs.IsVisible = _selectedNamedGraphs != null;
            LblNamedGraphs.Text = _selectedNamedGraphs == null ? string.Empty :
                "named graphs: " + string.Join(", ", _selectedNamedGraphs.Keys);
            ToolTip.SetTip(LblNamedGraphs, _selectedNamedGraphs == null ? null :
                string.Join("\n", _selectedNamedGraphs.Select(pair => $"{pair.Key}: {pair.Value}")));
            ChkUiGraph.IsEnabled = preset == null || !string.IsNullOrWhiteSpace(preset.UiGraph);
            ChkUiGraph.IsChecked = ChkUiGraph.IsEnabled && _config.ChkUiGraph;
        }
        finally { _isApplyingPreset = false; }
    }

    private void UpdateHeroDetailsFromPath(string vmdlPath)
    {
        try
        {
            if (TrySelectNpcPresetForPath(vmdlPath))
                return;

            var heroName = CsdkPaths.DetectHeroFromPath(vmdlPath);
            var db = HeroDatabase.GetDatabase();
            if (string.IsNullOrEmpty(heroName) || !db.TryGetValue(heroName, out var preset))
            {
                CmbHeroPreset.SelectedIndex = 0;

                ApplyPresetPaths(null);
                return;
            }

            ApplyPresetPaths(preset);

            var match = _presetChoices.FirstOrDefault(choice =>
                choice.Key.Equals(heroName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var hero = HeroPresetMatcher.FindKnownHero(heroName, preset);
                if (hero != null)
                    match = _presetChoices.FirstOrDefault(choice =>
                        choice.Hero?.HeroKey.Equals(hero.HeroKey, StringComparison.OrdinalIgnoreCase) == true);
            }
            if (match != null && !ReferenceEquals(CmbHeroPreset.SelectedItem, match))
                CmbHeroPreset.SelectedItem = match;
        }
        catch (Exception ex)
        {
            Log($"[presets] could not detect the hero for {Path.GetFileName(vmdlPath)}: {ex.Message}");
        }
    }

    private bool TrySelectNpcPresetForPath(string vmdlPath)
    {
        if (_npcPresetChoices.Count == 0) return false;

        var detectedKey = CsdkPaths.DetectHeroFromPath(vmdlPath);
        var match = !string.IsNullOrWhiteSpace(detectedKey)
            ? _npcPresetChoices.FirstOrDefault(choice =>
                choice.Key.Equals(detectedKey, StringComparison.OrdinalIgnoreCase))
            : null;

        if (match == null)
        {
            var (_, _, subpath) = CsdkPaths.ParseCsdkPath(
                vmdlPath, TxtCitadelPath.Text?.Trim());
            var sourcePath = subpath.Replace('\\', '/');
            if (sourcePath.EndsWith(".vmdl", StringComparison.OrdinalIgnoreCase))
                sourcePath += "_c";
            match = _npcPresetChoices.FirstOrDefault(choice =>
                !string.IsNullOrWhiteSpace(choice.SourcePath) &&
                choice.SourcePath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        }

        if (match?.Preset == null) return false;

        CmbNpcPreset.SelectedItem = match;
        ApplyPresetPaths(match.Preset);
        return true;
    }

    private void CmbHeroPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingPreset) return;

        if (CmbHeroPreset.SelectedItem is HeroPresetChoice choice)
        {
            if (choice.IsAuto)
            {
                var path = GetResolvedTargetPath();
                if (!string.IsNullOrEmpty(path)) UpdateHeroDetailsFromPath(path);
                else ApplyPresetPaths(null);
            }
            else
            {
                var db = HeroDatabase.GetVisiblePresets();
                if (db.TryGetValue(choice.Key, out var preset))
                {
                    ApplyPresetPaths(preset);
                }
            }
        }
    }

    private void CmbNpcPreset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingPreset) return;
        if (CmbNpcPreset.SelectedItem is not NpcPresetChoice choice) return;

        if (choice.IsAuto)
        {
            var path = GetResolvedTargetPath();
            if (!string.IsNullOrEmpty(path) && !TrySelectNpcPresetForPath(path))
                ApplyPresetPaths(null);
            return;
        }

        if (choice.Preset != null)
        {
            CmbHeroPreset.SelectedIndex = 0;
            ApplyPresetPaths(choice.Preset);
        }
    }

    // -----------------------------------------------------------------
    // EVENT HANDLERS
    // -----------------------------------------------------------------
    private void ChkShowNpcPresets_Click(object? sender, RoutedEventArgs e)
    {
        NpcPresetPanel.IsVisible = ChkShowNpcPresets.IsChecked == true;
    }

    private async void BtnScanNpcPresets_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select Deadlock pak01_dir.vpk",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Deadlock VPK (*.vpk)")
                {
                    Patterns = new[] { "pak01_dir.vpk", "*.vpk" }
                }
            }
        });
        if (files.Count > 0)
            await LoadNpcPresetsAsync(files[0].Path.LocalPath);
    }
}
