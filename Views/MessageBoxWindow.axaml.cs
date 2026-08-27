using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace DeadlockVmdlCompiler.Views;

public partial class MessageBoxWindow : Window
{
    public bool Result { get; private set; }

    public MessageBoxWindow()
    {
        InitializeComponent();
    }

    public MessageBoxWindow(string title, string message, bool isConfirm = false, bool isError = false) : this()
    {
        Title = title;
        TxtTitle.Text = title;
        TxtMessage.Text = message;

        if (isConfirm)
        {
            BtnCancel.IsVisible = true;
            BtnOk.Content = "Yes";
            BtnCancel.Content = "No";
        }

        if (isError)
        {
            BorderIcon.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x1A, 0x1A));
            IconPath.Fill = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            IconPath.Data = Geometry.Parse("M12 2C6.48 2 2 6.48 2 12C2 17.52 6.48 22 12 22C17.52 22 22 17.52 22 12C22 6.48 17.52 2 12 2ZM13 17H11V15H13V17ZM13 13H11V7H13V13Z");
        }
    }

    private void BtnOk_Click(object? sender, RoutedEventArgs e)
    {
        Result = true;
        Close(true);
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close(false);
    }
}
