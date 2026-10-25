using System.Diagnostics;
using Compressarr.Core.Auth;
using Compressarr.Core.Config;
using Compressarr.Core.Orchestration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Compressarr.Desktop.ViewModels;

/// <summary>Backs the system tray icon's NativeMenu - the only UI Compressarr.Desktop has left.
/// Everything else lives in the browser; this just starts/stops monitoring and opens it.</summary>
public partial class AppTrayViewModel : ObservableObject
{
    private readonly IConfigStore _configStore;
    private readonly IRunLoopController _runLoopController;
    private readonly IWebAuth _webAuth;
    private readonly string _webUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopMonitoring))]
    public partial bool IsMonitoring { get; set; }

    // Mirrors IRunLoopController.IsStopping/StoppingChanged - the shared source of truth both
    // this tray menu and the web UI observe, so a stop requested from either surface (not just
    // this tray's own Stop Monitoring click) is reflected here too.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStopMonitoring))]
    [NotifyPropertyChangedFor(nameof(StopMenuHeader))]
    public partial bool IsStopping { get; set; }

    public bool CanStopMonitoring => IsMonitoring && !IsStopping;
    public string StopMenuHeader => IsStopping ? "Stopping after task" : "Stop Monitoring";

    public AppTrayViewModel(IConfigStore configStore, IRunLoopController runLoopController, IWebAuth webAuth, string webUrl)
    {
        _configStore = configStore;
        _runLoopController = runLoopController;
        _webAuth = webAuth;
        _webUrl = webUrl;

        _runLoopController.RunningChanged += running => IsMonitoring = running;
        _runLoopController.StoppingChanged += stopping => IsStopping = stopping;
        IsMonitoring = _runLoopController.IsRunning;
        IsStopping = _runLoopController.IsStopping;
    }

    [RelayCommand]
    private void OpenWebUi()
    {
        Process.Start(new ProcessStartInfo(_webUrl) { UseShellExecute = true });
    }

    [RelayCommand]
    private void StartMonitoring()
    {
        var config = _configStore.Load(AppPaths.GetConfigFilePath());
        _runLoopController.Start(config, TimeSpan.FromSeconds(Math.Max(5, config.Repeat.PollIntervalSeconds)));
    }

    [RelayCommand]
    private async Task StopMonitoringAsync()
    {
        // IsStopping is driven entirely by StoppingChanged (subscribed in the constructor) so it
        // reflects a stop requested from either surface, not just this command's own click.
        await _runLoopController.StopAsync();
    }

    /// <summary>The way back from a forgotten web password under "Authentication Required: Enabled": it puts the sign-in back to "Disabled for Local Addresses", so this computer and
    /// the local network can open Settings > Security and set a new password. Asked here, at the PC, because that is where the owner is. See docs/design-login.md.</summary>
    [RelayCommand]
    private async Task ResetWebLoginAsync()
    {
        var settings = _webAuth.GetSettings();
        if (settings.Method == AuthMethod.None || settings.Required == AuthRequired.DisabledForLocalAddresses)
        {
            await new Views.ConfirmWindow("Your local network can already use Compressarr without signing in. Only requests from outside your network need the username and password.\n\nTo change the password, open Settings > Security in the web interface.", "OK", null).ShowAsync();
            return;
        }

        var yes = await new Views.ConfirmWindow("Turn off the web login for your local network?\n\nAnyone on your own network will be able to use Compressarr without signing in (the way it works by default). Requests from outside your network will still need your username and password.\n\nYou can change this again in Settings > Security.", "Turn off", "Cancel").ShowAsync();
        if (yes) _webAuth.ResetToLocalOnly();
    }

    [RelayCommand]
    private void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }
}
