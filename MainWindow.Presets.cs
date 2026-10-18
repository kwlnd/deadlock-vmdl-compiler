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
        var presets = HeroDatabase.GetVisiblePresets();
        var listed = presets
            .Where(pair => !pair.Value.NamedGraphs.ContainsKey("Neutrals"))
            .Select(pair => new HeroPresetChoice(pair.Key,
                HeroPresetMatcher.FindKnownHero(pair.Key, pair.Value),
                detail: null))
            .ToList();
        var choices = listed.Where(choice => choice.Hero != null)
            .OrderBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // NPCs and neutrals follow the heroes in the same list: walkers, guardians, troopers and
        // so on, scanned from the game, then the neutral presets of the preset list. Both teams'
        // versions of a model with the same skeleton and graphs, like the troopers, share one entry.
        var npcs = new List<HeroPresetChoice>();
        foreach (var group in _scannedNpcPresets.GroupBy(npc =>
                     (PresetSignature(npc.Preset), NpcCatalog.DisplayName(npc.SourcePath, withoutTeam: true))))
        {
            var first = group.First();
            var sources = group.Select(npc => npc.SourcePath).ToList();
            // A preset list that names the model (a custom list's own paths) overrides the scan.
            var ownPreset = sources.Select(source => Path.GetFileNameWithoutExtension(source[..^2]))
                .Select(stem => presets.TryGetValue(stem, out var own) ? own : null)
                .FirstOrDefault(own => own != null);
            npcs.Add(new HeroPresetChoice(first.Key,
                detail: first.IsNeutral ? "neutral" : NpcCatalog.IsSummon(first.SourcePath) ? "summon" : "npc",
                preset: ownPreset ?? first.Preset,
                displayName: NpcCatalog.DisplayName(first.SourcePath, withoutTeam: sources.Count > 1),
                sourcePaths: sources));
        }
        var scannedModels = _scannedNpcPresets
            .Select(npc => Path.GetFileNameWithoutExtension(npc.SourcePath[..^2]))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, preset) in presets.Where(pair => pair.Value.NamedGraphs.ContainsKey("Neutrals") &&
                                                            !NpcCatalog.IsUnused(pair.Key)))
        {
            // The same neutral from the scan can differ in graphs (park watcher); list it once.
            if (scannedModels.Contains(key)) continue;
            npcs.Add(new HeroPresetChoice(key, detail: "neutral", displayName: NpcCatalog.DisplayName(key)));
        }
        choices.AddRange(npcs.OrderBy(choice => choice.Detail switch { "npc" => 0, "summon" => 1, _ => 2 })
            .ThenBy(choice => choice.DisplayName, StringComparer.OrdinalIgnoreCase));
        // A custom list can still name an NPC the game scan lists (grey_talon_owl...): keep one.
        var npcModels = npcs.SelectMany(choice => choice.SourcePaths.Append(choice.Key))
            .Select(path => Path.GetFileNameWithoutExtension(path.EndsWith("_c") ? path[..^2] : path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        choices.AddRange(listed.Where(choice => choice.Hero == null && !npcModels.Contains(choice.Key) &&
                                                !NpcCatalog.IsUnused(choice.Key))
            .OrderBy(choice => choice.Key, StringComparer.OrdinalIgnoreCase));
        choices.Insert(0, new HeroPresetChoice(string.Empty, isAuto: true));
        foreach (var choice in choices)
        {
            if (choice.Hero != null && _presetPortraits.TryGetValue(choice.Hero.HeroKey, out var portrait))
                choice.Portrait = portrait;
            else if (NpcIconFor(choice) is { } icon)
                choice.Portrait = icon;
        }
        _presetChoices = choices;
        CmbHeroPreset.ItemsSource = choices;
        CmbHeroPreset.SelectedItem = choices.FirstOrDefault(choice => choice.Key == previousKey) ?? choices[0];
        var source = HeroDatabase.ActiveCustomFilePath;
        LblHeroPathsSource.Text = $"{(source == null ? "built-in list" : Path.GetFileName(source))} · {choices.Count - 1} presets";
        ToolTip.SetTip(LblHeroPathsSource, source ?? HeroDatabase.CustomLoadError ?? "Presets bundled inside this EXE.");
        BtnDefaultHeroPaths.IsVisible = source != null || !string.IsNullOrWhiteSpace(_config.HeroPathsFile);
    }

    private Bitmap? NpcIconFor(HeroPresetChoice choice) =>
        choice.IsNpc && NpcCatalog.IconPath(choice.SourcePaths.FirstOrDefault() ?? choice.Key) is { } path &&
        _npcIcons.TryGetValue(path, out var icon) ? icon : null;

    /// <summary>Loads the NPC HUD icons from the game for the NPC entries of the preset list.</summary>
    private async Task LoadNpcIconsAsync(string vpkPath)
    {
        try
        {
            var pngs = await Task.Run(() => HeroIconLoader.LoadImages(vpkPath,
                NpcCatalog.IconPaths.Where(path => !NpcCatalog.IsBundled(path))));
            if (!IsVisible) return;
            var icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, png) in pngs)
            {
                using var input = new MemoryStream(png);
                icons[path] = new Bitmap(input);
            }
            foreach (var path in NpcCatalog.IconPaths.Where(NpcCatalog.IsBundled))
                icons[path] = new Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(path)));
            foreach (var old in _npcIcons.Values) old.Dispose();
            _npcIcons = icons;
            foreach (var choice in _presetChoices)
                if (choice.Hero == null && NpcIconFor(choice) is { } icon)
                    choice.Portrait = icon;
        }
        catch (Exception ex)
        {
            Log($"[npc presets] icons unavailable: {ex.Message}");
        }
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
        _npcScanPending = true;
        try
        {
            var result = await VpkHeroScanner.ScanVpkForNpcsAsync(vpkPath);
            if (!IsVisible) return;
            _npcScanPending = false;

            _scannedNpcPresets = result.Presets;
            PopulateHeroPresets();
            _ = LoadNpcIconsAsync(vpkPath);
            var currentTarget = GetResolvedTargetPath();
            if (!string.IsNullOrWhiteSpace(currentTarget))
                UpdateHeroDetailsFromPath(currentTarget);
            Log($"[npc presets] {result.Message}");
        }
        catch (Exception ex)
        {
            _npcScanPending = false;
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
            var match = FindPresetForModel(vmdlPath, out var fromContent);
            if (match == null)
            {
                SelectPresetQuietly(_presetChoices.FirstOrDefault());
                ApplyPresetPaths(null);
                // Detection runs again once the NPC list arrives; only then is a miss final.
                if (!_npcScanPending)
                    Log($"[presets] could not tell which hero or npc {Path.GetFileName(vmdlPath)} is; pick its preset in the list.");
                return;
            }

            SelectPresetQuietly(match);
            ApplyPresetPaths(PresetFor(match));
            if (fromContent)
                Log($"[presets] {Path.GetFileName(vmdlPath)}: detected {match.DisplayName} from the files the model uses.");
        }
        catch (Exception ex)
        {
            Log($"[presets] could not detect the hero for {Path.GetFileName(vmdlPath)}: {ex.Message}");
        }
    }

    private void SelectPresetQuietly(HeroPresetChoice? choice)
    {
        if (choice == null || ReferenceEquals(CmbHeroPreset.SelectedItem, choice)) return;
        _isApplyingPreset = true;
        try { CmbHeroPreset.SelectedItem = choice; }
        finally { _isApplyingPreset = false; }
    }

    private static HeroPreset? PresetFor(HeroPresetChoice choice) =>
        choice.Preset ?? (HeroDatabase.GetVisiblePresets().TryGetValue(choice.Key, out var preset) ? preset : null);

    /// <summary>
    /// The preset of a model: by its path, then by the game model it replaces, then by the files
    /// its .vmdl uses (skeleton, meshes, materials), for addons whose folders are named freely.
    /// </summary>
    private HeroPresetChoice? FindPresetForModel(string vmdlPath, out bool fromContent)
    {
        fromContent = false;
        var choices = _presetChoices.Where(choice => !choice.IsAuto).ToList();
        HeroPresetChoice? ForKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var exact = choices.FirstOrDefault(choice => choice.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var hero = HeroDatabase.GetDatabase().TryGetValue(key, out var preset)
                ? HeroPresetMatcher.FindKnownHero(key, preset) : null;
            return hero == null ? null : choices.FirstOrDefault(choice =>
                choice.Hero?.HeroKey.Equals(hero.HeroKey, StringComparison.OrdinalIgnoreCase) == true);
        }

        var (_, _, subpath) = CsdkPaths.ParseCsdkPath(vmdlPath, TxtCitadelPath.Text?.Trim());
        var gamePath = subpath.Replace('\\', '/') + "_c";
        var byPath = choices.FirstOrDefault(choice =>
                         choice.SourcePaths.Contains(gamePath, StringComparer.OrdinalIgnoreCase)) ??
                     ForKey(CsdkPaths.DetectHeroFromPath(vmdlPath));
        if (byPath != null) return byPath;

        string content;
        try { content = File.ReadAllText(vmdlPath).Replace('\\', '/'); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        // A model compiled before already names its skeleton; that is the strongest hint. Presets
        // can share a skeleton (Viscous uses Kelvin's), so the named graphs pick among them, and a
        // tie is left to the other files the model uses.
        var bySkeletonAll = choices.Where(choice => PresetFor(choice) is { Skel.Length: > 0 } preset &&
                                                    content.Contains(preset.Skel, StringComparison.OrdinalIgnoreCase)).ToList();
        var bySkeletonGraph = bySkeletonAll.Where(choice => PresetFor(choice) is { } preset &&
                new[] { preset.Graph, preset.UiGraph }.Concat(preset.NamedGraphs.Values)
                    .Any(graph => graph.Length > 0 && content.Contains(graph, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var bySkeleton = bySkeletonAll.Count == 1 ? bySkeletonAll[0]
            : bySkeletonGraph.Count == 1 ? bySkeletonGraph[0] : null;
        var best = bySkeleton ?? System.Text.RegularExpressions.Regex.Matches(content, @"models/[A-Za-z0-9_./-]+")
            .Select(reference => choices.FirstOrDefault(choice => choice.SourcePaths.Any(source =>
                                     reference.Value.StartsWith(Path.GetDirectoryName(source)!.Replace('\\', '/') + "/",
                                         StringComparison.OrdinalIgnoreCase))) ??
                                 ForKey(CsdkPaths.DetectHeroFromPath(reference.Value)))
            .OfType<HeroPresetChoice>()
            .GroupBy(choice => choice)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();
        fromContent = best != null;
        return best;
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
            else if (PresetFor(choice) is { } preset)
            {
                ApplyPresetPaths(preset);
            }
        }
    }
}
