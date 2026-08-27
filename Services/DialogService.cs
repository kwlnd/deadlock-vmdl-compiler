using System.Threading.Tasks;
using Avalonia.Controls;
using DeadlockVmdlCompiler.Views;

namespace DeadlockVmdlCompiler.Services;

public static class DialogService
{
    public static async Task ShowInfoAsync(Window owner, string title, string message)
    {
        var box = new MessageBoxWindow(title, message, isConfirm: false, isError: false);
        await box.ShowDialog(owner);
    }

    public static async Task ShowErrorAsync(Window owner, string title, string message)
    {
        var box = new MessageBoxWindow(title, message, isConfirm: false, isError: true);
        await box.ShowDialog(owner);
    }

    public static async Task<bool> ShowConfirmAsync(Window owner, string title, string message)
    {
        var box = new MessageBoxWindow(title, message, isConfirm: true, isError: false);
        await box.ShowDialog(owner);
        return box.Result;
    }
}
