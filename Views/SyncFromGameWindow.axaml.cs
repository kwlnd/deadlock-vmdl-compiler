using Avalonia.Controls;
using Avalonia.Interactivity;
using DeadlockVmdlCompiler.Services;

namespace DeadlockVmdlCompiler.Views;

public partial class SyncFromGameWindow : Window
{
    public VmdlPipeline.GameSyncOptions? Result { get; private set; }

    public SyncFromGameWindow()
    {
        InitializeComponent();
        foreach (var option in new[] { ChkFixFrame, ChkAttachments, ChkGameData, ChkHitboxes })
            option.IsCheckedChanged += (_, _) => BtnSync.IsEnabled = Options().NeedsGameModel || Options().FixAttachmentFrame;
    }

    public SyncFromGameWindow(string vmdlPath) : this()
    {
        // Show the game-relative path the model will be matched against.
        TxtModel.Text = GameModelSource.GetVpkPath(vmdlPath)?[..^2] ?? Path.GetFileName(vmdlPath);
        ToolTip.SetTip(TxtModel, vmdlPath);
    }

    private VmdlPipeline.GameSyncOptions Options() => new(
        ChkFixFrame.IsChecked == true,
        ChkAttachments.IsChecked == true,
        ChkGameData.IsChecked == true,
        ChkHitboxes.IsChecked == true);

    private void BtnSync_Click(object? sender, RoutedEventArgs e)
    {
        Result = Options();
        Close();
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e) => Close();
}
