using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Compressarr.Core;
using Compressarr.Core.Config;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.Notifications;
using Compressarr.Core.Startup;
using Compressarr.Desktop.ViewModels;
using Compressarr.Desktop.Views;
using Compressarr.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Desktop;

public partial class App : Application
{
    /// <summary>The splash stays at least this long, so on a fast machine it is a clean flash rather than a flicker.</summary>
    private const int SplashMinimumMs = 700;

    public IServiceProvider Services { get; private set; } = null!;

    private WebApplication? _webApp;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        SplashWindow? splash = null;
        var shownAt = Stopwatch.StartNew();

        if (desktop is not null)
        {
            // Set before any window exists: the splash would otherwise be the only window, and closing it would end the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => _webApp?.StopAsync().GetAwaiter().GetResult();

            // The splash is for the user opening Compressarr, shown before anything else happens. A start at login (--autostart, see
            // RunKeyDecider) has nobody waiting for it, so it starts quietly.
            if (desktop.Args?.Contains(RunKeyDecider.AutostartArgument, StringComparer.OrdinalIgnoreCase) != true)
            {
                splash = new SplashWindow();
                splash.Show();
            }
        }

        // Everything else waits one tick, so the splash gets its first frame before the start-up work begins.
        Dispatcher.UIThread.Post(() => _ = StartAsync(desktop, splash, shownAt), DispatcherPriority.Background);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The start-up work that used to run inside OnFrameworkInitializationCompleted: build and start the web host, the schedulers and the
    /// run loop - with the heavy part (building the host, profile setup) on a background thread so the splash stays alive - then close the splash and
    /// open the Monitor.</summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime? desktop, SplashWindow? splash, Stopwatch shownAt)
    {
        try
        {
            splash?.SetStatus("Loading your settings...");

            // A lightweight, direct config read (same pattern the old MainViewModel's design-time
            // constructor used) just to know which port to bind before the DI container exists -
            // MainViewModel's replacement endpoints reload config themselves per-request afterward.
            var earlyConfig = new JsonConfigStore().Load(AppPaths.GetConfigFilePath());
            var port = earlyConfig.Web.Port;
            var webUrl = $"http://localhost:{port}/";

            splash?.SetStatus("Getting things ready...");
            await Task.Run(() => BuildHost(port, webUrl));

            if (desktop is not null)
            {
                // TrayIcon is declared in App.axaml via the Application-level TrayIcon.Icons attached
                // property, so it inherits its DataContext from the Application's own logical tree -
                // setting it here is what makes the NativeMenuItem Command bindings resolve.
                DataContext = Services.GetRequiredService<AppTrayViewModel>();
            }

            splash?.SetStatus("Starting the web interface...");
            var webStartTask = WebMonitorStartup.StartAsync(_webApp!, Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>());

            // The first CPU reading of a process takes most of a second (Windows sets its counters up), and the Monitor asks for one the moment
            // it opens. Taken here, behind the splash, in parallel with the web server starting, so the Monitor does not wait for it.
            var cpuWarmUp = Task.Run(async () => { try { await Services.GetRequiredService<ICpuUsageSampler>().SampleAsync(); } catch { /* only a warm-up */ } });

            if (earlyConfig.Repeat.Monitor)
            {
                var loopController = Services.GetRequiredService<Compressarr.Core.Orchestration.IRunLoopController>();
                loopController.Start(earlyConfig, TimeSpan.FromSeconds(Math.Max(5, earlyConfig.Repeat.PollIntervalSeconds)));
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

            // Kestrel has to be listening before the Monitor opens (it would just show a connection-refused page), and the splash is the one
            // thing the user is looking at until then.
            splash?.SetStatus("Almost ready...");
            await Task.WhenAll(webStartTask, cpuWarmUp);

            var remaining = SplashMinimumMs - (int)shownAt.ElapsedMilliseconds;
            if (splash is not null && remaining > 0) await Task.Delay(remaining);
            splash?.Close();

            // Independent of Repeat.Monitor above - opens the browser regardless of whether
            // monitoring itself was also set to auto-start. Reuses IReportLauncher rather than a new
            // service: Process.Start(UseShellExecute: true) opens a URL in the OS default browser the
            // same way it opens a report file in its own default handler, no Windows-specific
            // shell-launch code needed.
            // The installer's "Launch Compressarr" passes --open-ui so a fresh install lands on the Monitor page.
            var openUiRequested = desktop?.Args?.Contains("--open-ui") == true;
            if (earlyConfig.Repeat.LaunchMonitorAtStartup || openUiRequested)
            {
                Services.GetRequiredService<Compressarr.Core.Reporting.IReportLauncher>().Open($"{webUrl}monitor.html");
            }
        }
        catch (Exception ex)
        {
            splash?.Close();
            try { Services?.GetService<Compressarr.Core.Logging.IRunLogger>()?.Log($"Compressarr failed to start: {ex.Message}", Compressarr.Core.Logging.LogSeverity.Error); }
            catch { /* nowhere left to report it */ }
        }
    }

    /// <summary>Builds the web host and does the one-time setup that has to be in place before it starts. Runs on a background thread.</summary>
    private void BuildHost(int port, string webUrl)
    {
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
            sp.GetRequiredService<Compressarr.Core.Auth.IWebAuth>(),
            webUrl));
#if WINDOWS
        builder.Services.AddSingleton<INotificationService, Notifications.WindowsNotificationService>();
#endif

        var webApp = builder.Build();
        // Before the static files and the endpoints: the allowed-hosts check, the cross-site check and the sign-in wall (see docs/design-login.md).
        Compressarr.Web.Auth.WebAuthExtensions.UseCompressarrWebAuth(webApp);
        // Monitor, not Settings (the plain UseDefaultFiles()/index.html default) - the tray icon's
        // "Open Web UI" and a bare http://localhost:{port}/ should land wherever a user checks in
        // most often day-to-day, which is the running queue, not the one-time setup page. Settings
        // itself is unaffected - it still lives at /index.html and every nav link to it already
        // points there explicitly (see nav.js), this only changes what an UNqualified "/" resolves
        // to.
        webApp.UseDefaultFiles(new Microsoft.AspNetCore.Builder.DefaultFilesOptions
        {
            DefaultFileNames = new List<string> { "monitor.html" }
        });
        webApp.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
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
        webApp.MapCompressarrEndpoints();

        _webApp = webApp;
        Services = webApp.Services;

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

        // The first start of 3.0 imports the old CSV history (and keeps the old reports) into the run archive. Done here, behind the splash, rather
        // than on the first page request. Every later start only opens the file. Best effort: a failure is remembered by the store, which then serves
        // the CSV as before, and is looked at again on the next start.
        try
        {
            var archive = Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
            if (!archive.IsAvailable())
            {
                Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>().Log(
                    $"The run history could not be opened in its database ({(archive as Compressarr.Core.Logging.SqliteRunHistoryStore)?.ImportFailure}); the old history file is used for now and nothing was removed.",
                    Compressarr.Core.Logging.LogSeverity.Error);
            }
        }
        catch { }

        // The queue lives in its own database too: the first start moves compressarr.resume.json into it. A damaged database or file is set aside by the
        // store, which says so here (the log is where a user looks) and starts with an empty queue.
        try
        {
            var queueNotice = Services.GetRequiredService<Compressarr.Core.Conversion.SqliteResumeStateStore>().Prepare(Compressarr.Core.Config.AppPaths.GetResumeFilePath());
            if (queueNotice is not null)
            {
                Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>().Log(queueNotice, Compressarr.Core.Logging.LogSeverity.Error);
            }
        }
        catch { }

        // COMPRESSARR_RESET_LOGIN=1 (a container, a service) puts the sign-in back to "Disabled for Local Addresses" once, for a forgotten password under "Enabled".
        try
        {
            if (Services.GetRequiredService<Compressarr.Core.Auth.IWebAuth>().ApplyEnvironmentReset())
            {
                Services.GetRequiredService<Compressarr.Core.Logging.IRunLogger>().Log("COMPRESSARR_RESET_LOGIN is set: the web sign-in was put back to \"Disabled for Local Addresses\". Remove the variable once you have set a new password.");
            }
        }
        catch { }

        // The values the app itself writes (last backup, digest-sent days, the History viewed-through marker) used to sit in the settings file. Moved into
        // the state database before the schedulers start, so the first backup and digest check after an upgrade see them. Best effort: a value left in the
        // file is moved on the next start.
        try { Services.GetRequiredService<Compressarr.Core.Config.IAppState>().AdoptLegacyValues(); }
        catch { }

        // The keys, tokens and webhook addresses used to sit in the settings file too: moved into the state database (and out of the file), best effort like the above.
        try { Services.GetRequiredService<Compressarr.Core.Config.SecretsConfigStore>().AdoptLegacySecrets(); }
        catch { }
    }
}
