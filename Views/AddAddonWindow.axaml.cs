using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using DeadlockVmdlCompiler.Models;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler.Views;

public partial class AddAddonWindow : Window
{
    private readonly string _contentAddonsDirectory;
    private readonly string _pak01VpkPath;
    private readonly Action<string> _onLog;
    private CancellationTokenSource? _cancellation;
    private bool _isExporting;
    private readonly List<HeroChoice> _heroChoices = new();
    // A mod's VPK to export from instead of the game; null exports a game model.
    private string? _sourceVpkPath;

    public AddonCreationResult? Result { get; private set; }

    public AddAddonWindow() : this(string.Empty, string.Empty, _ => { }) { }

    public AddAddonWindow(string contentAddonsDirectory, string pak01VpkPath, Action<string> onLog)
    {
        InitializeComponent();
        _contentAddonsDirectory = contentAddonsDirectory;
        _pak01VpkPath = pak01VpkPath;
        _onLog = onLog;
        foreach (var hero in DeadlockHeroCatalog.GetExportableModels())
            _heroChoices.Add(new HeroChoice(hero));
        CmbHero.ItemsSource = _heroChoices;
        CmbHero.ItemFilter = (query, item) => item is HeroChoice choice &&
            (string.IsNullOrWhiteSpace(query) ||
             choice.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        Opened += async (_, _) =>
        {
            TxtAddonName.Focus();
            await LoadHeroIconsAsync();
        };
        Closed += (_, _) =>
        {
            foreach (var choice in _heroChoices)
                choice.Icon?.Dispose();
        };
        Closing += (_, e) =>
        {
            if (_isExporting)
            {
                e.Cancel = true;
                _cancellation?.Cancel();
                TxtProgress.Text = "Cancelling after the current export step...";
            }
        };
    }

    private void CmbHero_GotFocus(object? sender, GotFocusEventArgs e)
    {
        if (!_isExporting)
            CmbHero.IsDropDownOpen = true;
    }

    private void CmbHero_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdateSelectedHeroPortrait();

    private void CmbHero_TextChanged(object? sender, TextChangedEventArgs e) =>
        UpdateSelectedHeroPortrait();

    private void UpdateSelectedHeroPortrait()
    {
        if (SelectedHeroPortrait is null) return;

        var choice = CmbHero.SelectedItem as HeroChoice;
        if (!string.Equals(CmbHero.Text?.Trim(), choice?.DisplayName, StringComparison.OrdinalIgnoreCase))
            choice = null;

        SelectedHeroPortrait.IsVisible = choice != null;
        ImgSelectedHero.Source = choice?.Icon;
        ImgSelectedHero.IsVisible = choice?.Icon != null;
        SelectedHeroFallback.IsVisible = choice != null && choice.Icon == null;
    }

    private async Task LoadHeroIconsAsync()
    {
        if (!File.Exists(_pak01VpkPath)) return;
        try
        {
            var pngs = await Task.Run(() => HeroIconLoader.LoadSmallPortraits(
                _pak01VpkPath, DeadlockHeroCatalog.GetHeroes()));
            if (!IsVisible) return;

            foreach (var choice in _heroChoices)
            {
                if (!pngs.TryGetValue(choice.Hero.HeroKey, out var png)) continue;
                using var input = new MemoryStream(png);
                choice.Icon = new Bitmap(input);
            }
            UpdateSelectedHeroPortrait();
            var available = DeadlockHeroCatalog.GetHeroes().Count(hero => hero.IconVpkPath != null);
            if (pngs.Count != available)
                _onLog($"[add addon] loaded {pngs.Count}/{available} hero portraits from pak01_dir.vpk.");
        }
        catch (Exception ex)
        {
            _onLog($"[add addon] hero portraits unavailable: {ex.Message}");
        }
    }

    private async void BtnSourceVpk_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "select a mod's vpk",
            FileTypeFilter = new[] { new FilePickerFileType("valve pack file (*.vpk)") { Patterns = new[] { "*.vpk" } } }
        });
        if (files.Count == 0) return;
        var path = files[0].Path.LocalPath;

        IReadOnlyList<string> models;
        try
        {
            models = await Task.Run(() => AddonCreationService.ListModels(path));
        }
        catch (Exception ex)
        {
            ShowError($"Could not read the VPK: {ex.Message}");
            return;
        }
        if (models.Count == 0)
        {
            ShowError("The selected VPK contains no compiled models (.vmdl_c).");
            return;
        }

        _sourceVpkPath = path;
        TxtSourceVpk.Text = path;
        CmbVpkModel.ItemsSource = models;
        CmbVpkModel.SelectedIndex = 0;
        TxtError.IsVisible = false;
        UpdateSourceMode();
    }

    private void BtnUseGame_Click(object? sender, RoutedEventArgs e)
    {
        _sourceVpkPath = null;
        TxtSourceVpk.Text = "Deadlock game files";
        CmbVpkModel.ItemsSource = null;
        TxtError.IsVisible = false;
        UpdateSourceMode();
    }

    private void UpdateSourceMode()
    {
        var fromVpk = _sourceVpkPath != null;
        PanelVpkModel.IsVisible = fromVpk;
        PanelHero.IsVisible = !fromVpk;
        BtnUseGame.IsVisible = fromVpk;
        TxtSourceHint.Text = fromVpk
            ? "Missing skeletons, animations and stock materials are taken from the game."
            : "Export from the game, or pick a mod's .vpk to export its model.";
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.IsVisible = true;
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_isExporting)
        {
            _cancellation?.Cancel();
            BtnCancel.IsEnabled = false;
            TxtProgress.Text = "Cancelling after the current export step...";
        }
        else Close();
    }

    private async void BtnCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (_isExporting) return;
        var name = TxtAddonName.Text?.Trim() ?? string.Empty;
        var error = AddonCreationService.ValidateName(name);
        var sourceVpk = _sourceVpkPath;
        var vpkModel = CmbVpkModel.SelectedItem as string;
        DeadlockHeroModel? hero = null;
        if (sourceVpk != null)
        {
            if (vpkModel is null) error = "Choose a model from the VPK.";
        }
        else
        {
            var typedHero = CmbHero.Text?.Trim();
            var selectedChoice = CmbHero.SelectedItem as HeroChoice;
            if (!string.Equals(typedHero, selectedChoice?.DisplayName, StringComparison.OrdinalIgnoreCase))
                selectedChoice = _heroChoices.Find(choice =>
                    string.Equals(choice.DisplayName, typedHero, StringComparison.OrdinalIgnoreCase));
            hero = selectedChoice?.Hero;
            if (hero is null)
                error = "Choose a character from the list or type its full name.";
        }
        if (error != null)
        {
            ShowError(error);
            if (sourceVpk == null && hero is null) CmbHero.Focus();
            else if (sourceVpk != null && vpkModel is null) CmbVpkModel.Focus();
            else TxtAddonName.Focus();
            return;
        }

        _isExporting = true;
        _cancellation = new CancellationTokenSource();
        BtnCreate.IsEnabled = false;
        SetInputsEnabled(false);
        TxtError.IsVisible = false;
        PanelProgress.IsVisible = true;
        TxtProgress.Text = $"Exporting {(hero?.DisplayName ?? Path.GetFileName(vpkModel))}...";

        try
        {
            var progress = new Progress<DecompileProgress>(p =>
                TxtProgress.Text = $"{p.ExtractedCount} files: {p.CurrentFile}");
            Result = sourceVpk != null
                ? await AddonCreationService.CreateFromVpkAsync(
                    _contentAddonsDirectory, sourceVpk, vpkModel!, _pak01VpkPath, name,
                    progress, _onLog, _cancellation.Token)
                : await AddonCreationService.CreateAsync(
                    _contentAddonsDirectory, _pak01VpkPath, hero!, name,
                    progress, _onLog, _cancellation.Token);
            _isExporting = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            _onLog("[add addon] export cancelled; temporary files removed.");
            _isExporting = false;
            Close();
        }
        catch (Exception ex)
        {
            _onLog($"[add addon error] {ex.Message}");
            ShowError(ex.Message);
            PanelProgress.IsVisible = false;
        }
        finally
        {
            _isExporting = false;
            _cancellation?.Dispose();
            _cancellation = null;
            BtnCreate.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            SetInputsEnabled(true);
        }
    }

    private void SetInputsEnabled(bool enabled)
    {
        CmbHero.IsEnabled = enabled;
        CmbVpkModel.IsEnabled = enabled;
        BtnSourceVpk.IsEnabled = enabled;
        BtnUseGame.IsEnabled = enabled;
        TxtAddonName.IsEnabled = enabled;
    }
}

public sealed class HeroChoice : INotifyPropertyChanged
{
    private Bitmap? _icon;

    public HeroChoice(DeadlockHeroModel hero) => Hero = hero;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DeadlockHeroModel Hero { get; }
    public string DisplayName => Hero.DisplayName;
    public override string ToString() => DisplayName;

    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon?.Dispose();
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }
}
