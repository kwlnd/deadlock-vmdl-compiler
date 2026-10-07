using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
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
        var typedHero = CmbHero.Text?.Trim();
        var selectedChoice = CmbHero.SelectedItem as HeroChoice;
        if (!string.Equals(typedHero, selectedChoice?.DisplayName, StringComparison.OrdinalIgnoreCase))
            selectedChoice = _heroChoices.Find(choice =>
                string.Equals(choice.DisplayName, typedHero, StringComparison.OrdinalIgnoreCase));
        var hero = selectedChoice?.Hero;
        if (hero is null)
            error = "Choose a character from the list or type its full name.";
        if (error != null)
        {
            TxtError.Text = error;
            TxtError.IsVisible = true;
            if (hero is null) CmbHero.Focus();
            else TxtAddonName.Focus();
            return;
        }

        _isExporting = true;
        _cancellation = new CancellationTokenSource();
        BtnCreate.IsEnabled = false;
        CmbHero.IsEnabled = false;
        TxtAddonName.IsEnabled = false;
        TxtError.IsVisible = false;
        PanelProgress.IsVisible = true;
        TxtProgress.Text = $"Exporting {hero!.DisplayName}...";

        try
        {
            var progress = new Progress<DecompileProgress>(p =>
                TxtProgress.Text = $"{p.ExtractedCount} files: {p.CurrentFile}");
            Result = await AddonCreationService.CreateAsync(
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
            TxtError.Text = ex.Message;
            TxtError.IsVisible = true;
            PanelProgress.IsVisible = false;
        }
        finally
        {
            _isExporting = false;
            _cancellation?.Dispose();
            _cancellation = null;
            BtnCreate.IsEnabled = true;
            BtnCancel.IsEnabled = true;
            CmbHero.IsEnabled = true;
            TxtAddonName.IsEnabled = true;
        }
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
