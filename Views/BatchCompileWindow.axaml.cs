using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Views;

/// <summary>
/// Lists the models of an addon with a preset each before they are compiled together; detected
/// presets are preselected and any of them can be changed.
/// </summary>
public partial class BatchCompileWindow : Window
{
    private readonly IReadOnlyList<BatchCompileRow> _rows = [];

    /// <summary>True when the user started the compile with a preset for every model.</summary>
    public bool Confirmed { get; private set; }

    public BatchCompileWindow() => InitializeComponent();

    public BatchCompileWindow(string addonName, IReadOnlyList<BatchCompileRow> rows) : this()
    {
        _rows = rows;
        TxtTitle.Text = $"compile all models · {addonName}";
        ListModels.ItemsSource = rows;
        foreach (var row in rows)
            row.PropertyChanged += (_, _) => UpdateCompileButton();
        UpdateCompileButton();
    }

    private void UpdateCompileButton()
    {
        var missing = _rows.Count(row => row.Selected == null);
        BtnCompile.IsEnabled = missing == 0;
        BtnCompile.Content = $"compile {_rows.Count} models";
        TxtHint.Text = missing > 0
            ? $"{missing} model(s) have no preset yet: pick one to compile."
            : "Every model stays protected after its compile, so one VPK holds them all.";
    }

    private void BtnCompile_Click(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}

/// <summary>One model of a batch compile and the preset it will be compiled with.</summary>
public sealed class BatchCompileRow : INotifyPropertyChanged
{
    private HeroPresetChoice? _selected;

    public BatchCompileRow(string path, string display, IReadOnlyList<HeroPresetChoice> choices,
        HeroPresetChoice? selected, string note)
    {
        Path = path;
        Display = display;
        Choices = choices;
        _selected = selected;
        Note = note;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }
    public string Display { get; }
    public string FileName => System.IO.Path.GetFileName(Display);
    public string Note { get; }
    public IReadOnlyList<HeroPresetChoice> Choices { get; }

    public HeroPresetChoice? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }
}
