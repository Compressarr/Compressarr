using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Compressarr.Core;
using Compressarr.Core.Config;
using Compressarr.Core.Notifications;
using Compressarr.Desktop.ViewModels;
using Compressarr.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Desktop;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    private WebApplication? _webApp;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A lightweight, direct config read (same pattern the old MainViewModel's design-time
        // constructor used) just to know which port to bind before the DI container exists -
        // MainViewModel's replacement endpoints reload config themselves per-request afterward.
        var earlyConfig = new JsonConfigStore().Load(AppPaths.GetConfigFilePath());
        var port = earlyConfig.Web.Port;
        var webUrl = $"http://localhost:{port}/";

        // ContentRootPath must be pinned to the assembly's own directory, not left to default to
        // Directory.GetCurrentDirectory() - `dotnet run` sets the working directory to the
        // project's *source* folder (no wwwroot there at all; wwwroot only exists in the build
        // output), which made UseStaticFiles()/UseDefaultFiles() 404 on everything when launched
        // that way. AppContext.BaseDirectory always points at the running assembly's own folder
        // regardless of how the process was started (dotnet run, double-click, Start-Process with
        // no explicit working directory, etc).
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        builder.Services.AddCompressarrCore();
        builder.Services.AddCompressarrWeb();
        builder.Services.AddSingleton(sp => new AppTrayViewModel(
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<Compressarr.Core.Orchestration.IRunLoopController>(),
            webUrl));
#if WINDOWS
        builder.Services.AddSingleton<INotificationService, Notifications.WindowsNotificationService>();
#endif

        _webApp = builder.Build();
        // Monitor, not Settings (the plain UseDefaultFiles()/index.html default) - the tray icon's
        // "Open Web UI" and a bare http://localhost:{port}/ should land wherever a user checks in
        // most often day-to-day, which is the running queue, not the one-time setup page. Settings
        // itself is unaffected - it still lives at /index.html and every nav link to it already
        // points there explicitly (see nav.js), this only changes what an UNqualified "/" resolves
        // to.
        _webApp.UseDefaultFiles(new Microsoft.AspNetCore.Builder.DefaultFilesOptions
        {
            DefaultFileNames = new List<string> { "monitor.html" }
        });
        _webApp.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
        {
            // No explicit Cache-Control means browsers fall back to heuristic caching off
            // Last-Modified, which can and does serve a stale wwwroot page after a rebuild - a
            // real trap during local iteration (and for the same reason, LAN clients checking a
            // long-running instance). This app is served from a single local Kestrel instance
            // with no CDN in front of it, so there's no caching benefit worth trading staleness
            // for.
            OnPrepareResponse = ctx =>
            {
                // The service worker script is the one exception to the blanket no-store below -
                // no-cache still forces revalidation on every registration attempt (so an updated
                // sw.js is never missed), it just stops short of no-store's "never let the browser
                // cache this response at all." This is the standard recommendation for service
                // worker scripts specifically (see web.dev's own PWA guidance) independent of any
                // one browser's behavior - not verified against a real Chrome/Edge from this
                // session (see compressarr_pwa memory: the sandboxed preview browser available
                // here fails ALL service worker registration, even a trivial one, so it couldn't
                // confirm or rule out a no-store-specific issue either way).
                ctx.Context.Response.Headers.CacheControl = ctx.File.Name == "sw.js"
                    ? "no-cache"
                    : "no-cache, no-store, must-revalidate";
            }
        });
        _webApp.MapCompressarrEndpoints();

        Services = _webApp.Services;

        // 2.2: Compressarr only ever uses its own encoder profiles. Once, carry over the presets the
        // lanes used from HandBrake's presets.json; then make sure the generated file HandBrake is
        // handed is current. Best effort - a failure here is logged, and the file is checked again
        // before every encode anyway.
        try
        {
            var migration = Services.GetRequiredService<Compressarr.Core.Presets.IHandBrakeProfileMigration>();
            migration.RunIfNeeded();
            migration.RecoverMissing(); // presets that arrived with settings after the first start (copied config, restored backup)
            Services.GetRequiredService<Compressarr.Core.Presets.IHandBrakeProfileStore>().EnsureActiveFile();
        }
        catch (Exception ex)
        {
            Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>().Log($"Profile setup failed: {ex.Message}", Compressarr.Core.Logging.LogSeverity.Error);
        }

        // The "run at login" setting only wrote the registry when Settings was saved, so settings that arrived another
        // way (a restored backup, an import, a new machine) never got their login entry. Best effort.
        try
        {
            var startupConfig = Services.GetRequiredService<Compressarr.Core.Config.IConfigStore>().Load(Compressarr.Core.Config.AppPaths.GetConfigFilePath());
            Services.GetRequiredService<Compressarr.Core.Startup.IStartupRegistrationService>().Reconcile(startupConfig.Startup.RunAtLogin);
        }
        catch { }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => _webApp.StopAsync().GetAwaiter().GetResult();

            // TrayIcon is declared in App.axaml via the Application-level TrayIcon.Icons attached
            // property, so it inherits its DataContext from the Application's own logical tree -
            // setting it here is what makes the NativeMenuItem Command bindings resolve.
            DataContext = Services.GetRequiredService<AppTrayViewModel>();
        }

        var webStartTask = WebMonitorStartup.StartAsync(_webApp, Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>());

        if (earlyConfig.Repeat.Monitor)
        {
            var loopController = Services.GetRequiredService<Compressarr.Core.Orchestration.IRunLoopController>();
            loopController.Start(earlyConfig, TimeSpan.FromSeconds(Math.Max(5, earlyConfig.Repeat.PollIntervalSeconds)));
        }

        // Independent of Repeat.Monitor above - opens the browser regardless of whether
        // monitoring itself was also set to auto-start. Reuses IReportLauncher rather than a new
        // service: Process.Start(UseShellExecute: true) opens a URL in the OS default browser the
        // same way it opens a report file in its own default handler, no Windows-specific
        // shell-launch code needed. Waits on webStartTask first - Kestrel hasn't necessarily
        // finished binding yet at this point (StartAsync above is fire-and-forget), and opening
        // the browser before it has would just show a connection-refused page.
        // The installer's "Launch Compressarr" passes --open-ui so a fresh install lands on the Monitor page.
        var openUiRequested = ApplicationLifetime is IClassicDesktopStyleApplicationLifetime launchArgs && launchArgs.Args?.Contains("--open-ui") == true;
        if (earlyConfig.Repeat.LaunchMonitorAtStartup || openUiRequested)
        {
            var reportLauncher = Services.GetRequiredService<Compressarr.Core.Reporting.IReportLauncher>();
            _ = webStartTask.ContinueWith(_ => reportLauncher.Open($"{webUrl}monitor.html"), TaskScheduler.Default);
        }

        // Always started, unconditionally - no separate enable/disable setting, matching Sonarr's
        // own always-on backup posture (only the folder/interval/retention are configurable).
        Services.GetRequiredService<Compressarr.Core.Backup.IBackupScheduler>().Start();

        // Same always-on posture - each channel/toast's own Daily/Weekly checkboxes are the real
        // on/off switches, checked every tick, so there's nothing to gate Start() itself on.
        Services.GetRequiredService<Compressarr.Core.Notifications.IDigestScheduler>().Start();

        // Applies the optional day/night schedule (encode priority, off-hours suspend). Inert unless the
        // schedule is switched on in Settings, so it is started unconditionally. The run loop is passed
        // so a Stop Monitoring request can release an encode the schedule has frozen.
        Services.GetRequiredService<Compressarr.Core.Scheduling.IEncodeSchedule>()
            .Start(Services.GetRequiredService<Compressarr.Core.Orchestration.IRunLoopController>());

        // Checks immediately on startup (not just once a day) - a restart is the one moment most
        // likely to have just resolved a previously-flagged update, so the toolbar indicator
        // shouldn't need to wait out the rest of a day's cache to notice.
        Services.GetRequiredService<Compressarr.Core.Updates.IUpdateCheckService>().Start();

        // Announcements from the project: one request at start-up and then about daily, and only while the Settings checkbox is on
        // (the service looks at the setting itself, so turning it on or off later needs no restart).
        Services.GetRequiredService<Compressarr.Core.Announcements.IAnnouncementService>().Start();

        base.OnFrameworkInitializationCompleted();
    }
}
