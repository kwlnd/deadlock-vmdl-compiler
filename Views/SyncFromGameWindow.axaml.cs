using Avalonia.Controls;
using Avalonia.Interactivity;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler.Views;

public partial class SyncFromGameWindow : Window
{
    public VmdlPipeline.GameSyncOptions? Result { get; private set; }
    public bool AllModels { get; private set; }

    public SyncFromGameWindow()
    {
        InitializeComponent();
        foreach (var option in new[] { ChkFixFrame, ChkAttachments, ChkGameData })
            option.IsCheckedChanged += (_, _) => BtnSync.IsEnabled = Options().NeedsGameModel || Options().FixAttachmentFrame;
        ChkAllModels.IsCheckedChanged += (_, _) => TxtModel.Opacity = ChkAllModels.IsChecked == true ? 0.45 : 1;
    }

    public SyncFromGameWindow(string vmdlPath, int modelCount = 0) : this()
    {
        // Show the game-relative path the model will be matched against.
        TxtModel.Text = GameModelSource.GetVpkPath(vmdlPath)?[..^2] ?? Path.GetFileName(vmdlPath);
        ToolTip.SetTip(TxtModel, vmdlPath);
        if (modelCount > 1)
        {
            ChkAllModels.IsVisible = true;
            TxtAllModels.Text = $"apply to every model in all addons ({modelCount})";
        }
    }

    private VmdlPipeline.GameSyncOptions Options() => new(
        ChkFixFrame.IsChecked == true,
        ChkAttachments.IsChecked == true,
        ChkGameData.IsChecked == true);

    private void BtnSync_Click(object? sender, RoutedEventArgs e)
    {
        Result = Options();
        AllModels = ChkAllModels.IsChecked == true;
        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
