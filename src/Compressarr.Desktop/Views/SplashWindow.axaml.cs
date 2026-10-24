using System.Reflection;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Compressarr.Desktop.Views;

/// <summary>The window shown while Compressarr starts: logo, name, version and what it is doing. Shown before anything else happens and closed by
/// App just before the Monitor opens, so opening the program is answered at once even though the background work takes a few seconds.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("VersionText")!.Text = VersionLabel();
    }

    /// <summary>"Starting...", "Almost ready...": what is happening now. Safe to call from the UI thread only.</summary>
    public void SetStatus(string text) => this.FindControl<TextBlock>("StatusText")!.Text = text;

    /// <summary>"v2.2.0-rc.3": the running version with its pre-release tag, without the build metadata after '+'.</summary>
    internal static string VersionLabel()
    {
        var assembly = typeof(SplashWindow).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return "";
        var plus = text.IndexOf('+');
        return "v" + (plus >= 0 ? text[..plus] : text);
    }
}
