using Compressarr.Core.Arr;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Dependencies;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Queue;
using Compressarr.Core.Scheduling;
using Compressarr.Core.Presets;
using Compressarr.Core.Reporting;
using Compressarr.Core.Routing;
using Compressarr.Core.Startup;
using Compressarr.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers every Compressarr.Core service behind its interface. Any host
    /// (Desktop today; a web server or CLI in a later phase) wires this up the same way -
    /// nothing here is Avalonia- or platform-specific.</summary>
    public static IServiceCollection AddCompressarrCore(this IServiceCollection services)
    {
        services.AddHttpClient();

        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IPathExpander, PathExpander>();
        services.AddSingleton<IHandBrakeProfileStore, HandBrakeProfileStore>();
        services.AddSingleton<HandBrakePresetService>();
        services.AddSingleton<IEncoderPresetService>(sp => sp.GetRequiredService<HandBrakePresetService>());
        services.AddSingleton<IFFmpegProfileStore, FFmpegProfileStore>();
        services.AddSingleton<FFmpegPresetService>();
        services.AddSingleton<IMediaProbe, FFprobeMediaProbe>();
        services.AddSingleton<FFmpegRunner>();
        services.AddSingleton<IEncoderResolver, EncoderResolver>();
        services.AddSingleton<IFFmpegCapabilityProbe, FFmpegCapabilityProbe>();
        services.AddSingleton<IFFmpegInstaller, FFmpegInstaller>();
        services.AddSingleton<Compressarr.Core.Catalog.ICatalogClient, Compressarr.Core.Catalog.CatalogClient>();
        services.AddSingleton<Compressarr.Core.Catalog.ICatalogService, Compressarr.Core.Catalog.CatalogService>();
        services.AddSingleton<IHandBrakeProfileMigration, HandBrakeProfileMigration>();
        services.AddSingleton<IHandBrakeProfileImporter, HandBrakeProfileImporter>();
        services.AddSingleton<IHandBrakeInstaller, HandBrakeInstaller>();

        services.AddSingleton<IVideoFileScanner, VideoFileScanner>();
        services.AddSingleton<IFileBotRunner, FileBotRunner>();
        services.AddSingleton<IActiveEncodeProcess, ActiveEncodeProcess>();
        services.AddSingleton<IEncodeSchedule, EncodeSchedule>();
        services.AddSingleton<HandBrakeProcessRunner>();
        services.AddSingleton<IEncoderRunner>(sp => sp.GetRequiredService<HandBrakeProcessRunner>());
        services.AddSingleton<IMetadataService, MetadataService>();
        services.AddSingleton<IResumeStateStore, JsonResumeStateStore>();
        services.AddSingleton<IFileRouter, FileRouter>();
        services.AddSingleton<ICompanionFileService, CompanionFileService>();
        services.AddSingleton<ITrashService>(sp => TrashServiceFactory.CreateForCurrentPlatform(new RunLoggerTrashWarningSink(sp.GetRequiredService<IRunLogger>())));

        services.AddSingleton<IArrClient, ArrClient>();
        services.AddSingleton<IArrUnmonitorService, ArrUnmonitorService>();

        services.AddSingleton<IRunLogger, FileRunLogger>();
        services.AddSingleton<IRunHistoryStore, CsvRunHistoryStore>();
        services.AddSingleton<IHistoryRollupCalculator, HistoryRollupCalculator>();
        services.AddSingleton<IHistoryThroughputEstimator, HistoryThroughputEstimator>();
        services.AddSingleton<IWebHistoryRollupCalculator, WebHistoryRollupCalculator>();
        services.AddSingleton<ICpuUsageSampler>(_ => CpuUsageSamplerFactory.CreateForCurrentPlatform());
        services.AddSingleton<IFileSystemBrowser, FileSystemBrowser>();

        services.AddSingleton<IHtmlReportGenerator, HtmlReportGenerator>();
        services.AddSingleton<IReportLauncher, ReportLauncher>();
        services.AddSingleton<INotificationService, NoOpNotificationService>();
        services.AddSingleton<IRunProgressReporter, NullRunProgressReporter>();

        // Generic Webhook is the one notifier that genuinely needs a configurable method and
        // arbitrary headers, so it alone keeps the older, fully-generic IWebhookSender. Every
        // fixed-destination notifier below uses the narrower INotificationHttpClient instead.
        services.AddSingleton<IWebhookSender, WebhookSender>();
        services.AddSingleton<INotifier, WebhookNotifier>();

        services.AddSingleton<INotificationHttpClient, NotificationHttpClient>();
        services.AddSingleton<INotifier, DiscordNotifier>();
        services.AddSingleton<INotifier, SlackNotifier>();
        services.AddSingleton<INotifier, TelegramNotifier>();
        services.AddSingleton<INotifier, PushoverNotifier>();
        services.AddSingleton<INotifier, NtfyNotifier>();
        services.AddSingleton<INotifier, GotifyNotifier>();
        services.AddSingleton<INotifier, NotifiarrNotifier>();
        services.AddSingleton<INotifier, IftttNotifier>();
        services.AddSingleton<INotificationDispatcher, NotificationDispatcher>();

        services.AddSingleton<IActiveRunController, ActiveRunController>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<IConversionOrchestrator, ConversionOrchestrator>();
        services.AddSingleton<IRunOrchestrator, RunOrchestrator>();
        services.AddSingleton<IRunLoopController, RunLoopController>();
        services.AddSingleton<IStartupRegistrationService>(_ => StartupRegistrationServiceFactory.CreateForCurrentPlatform());

        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IBackupScheduler, BackupScheduler>();
        services.AddSingleton<IDigestScheduler, DigestScheduler>();
        services.AddSingleton<IUpdateCheckService, UpdateCheckService>();

        return services;
    }
}
