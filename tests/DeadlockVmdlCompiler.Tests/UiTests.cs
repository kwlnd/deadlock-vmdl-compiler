using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeadlockVmdlCompiler.Services;
using DeadlockVmdlCompiler.Views;
using static DeadlockVmdlCompiler.Tests.TestSupport;

namespace DeadlockVmdlCompiler.Tests;

/// <summary>Windows driven without a screen, with the app's own styles.</summary>
public class UiTests
{
    [Fact]
    public async Task PickingAHeroInAddAddonDoesNotCrash()
    {
        // Avalonia 11.3.13 and later throw while closing the hero list after a click.
        HeroDatabase.UseBuiltInDatabase();
        using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        var picked = await session.Dispatch(() =>
        {
            using var temp = new TempDir();
            var window = new AddAddonWindow(temp.Path, string.Empty, _ => { }) { Width = 520, Height = 700 };
            window.Show();
            Pump();
            var heroes = window.FindControl<AutoCompleteBox>("CmbHero")!;
            heroes.Focus();
            Pump();
            Check(heroes.IsDropDownOpen, "Focusing the hero box did not open the hero list.");

            var item = heroes.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First()
                .Child!.GetVisualDescendants().OfType<ListBoxItem>().Skip(2).First();
            var root = (TopLevel)item.GetVisualRoot()!;
            var point = item.TranslatePoint(new Point(20, 10), root)!.Value;
            root.MouseDown(point, MouseButton.Left);
            root.MouseUp(point, MouseButton.Left);
            Pump();
            var result = heroes.SelectedItem as HeroChoice;
            window.Close();
            return result;
        }, CancellationToken.None);
        Check(picked != null, "Clicking a hero in the list did not select it.");
    }

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }
}

public class HeadlessApp : App
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    // The real app opens MainWindow here.
    public override void OnFrameworkInitializationCompleted() { }
}
