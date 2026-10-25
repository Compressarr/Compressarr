using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Compressarr.Desktop.Views;

/// <summary>A small question (or notice) for the tray menu, which has no window of its own to ask from. <see cref="ShowAsync"/> answers true only for the first button.</summary>
public partial class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();

    public ConfirmWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closed += (_, _) => _answer.TrySetResult(false);
    }

    /// <summary>With <paramref name="noText"/> null the window is a notice with one button.</summary>
    public ConfirmWindow(string message, string yesText, string? noText) : this()
    {
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("YesButton")!.Content = yesText;
        var no = this.FindControl<Button>("NoButton")!;
        if (noText is null) no.IsVisible = false; else no.Content = noText;
    }

    public Task<bool> ShowAsync()
    {
        Show();
        Activate();
        return _answer.Task;
    }

    private void OnYesClicked(object? sender, RoutedEventArgs e)
    {
        _answer.TrySetResult(true);
        Close();
    }

    private void OnNoClicked(object? sender, RoutedEventArgs e) => Close();
}
