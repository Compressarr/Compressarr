using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/settings", (IConfigStore configStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(new NotificationSettingsDto(
                config.Notifications.ToastEnabled,
                config.Notifications.ToastDigestDailyEnabled,
                config.Notifications.ToastDigestWeeklyEnabled,
                config.Notifications.ToastDigestDailyTime,
                config.Notifications.ToastDigestWeeklyTime,
                config.Notifications.ToastDigestWeeklyDay.ToString(),
                config.Notifications.MessageStyle.ToString(),
                config.Notifications.CustomTitleTemplate,
                config.Notifications.CustomBodyTemplate,
                config.Notifications.IncludeMachineName,
                config.Notifications.MachineName));
        });

        app.MapPut("/api/notifications/settings", (NotificationSettingsDto dto, IConfigStore configStore) =>
        {
            configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.Notifications.ToastEnabled = dto.ToastEnabled;
                config.Notifications.ToastDigestDailyEnabled = dto.ToastDigestDailyEnabled;
                config.Notifications.ToastDigestWeeklyEnabled = dto.ToastDigestWeeklyEnabled;
                config.Notifications.ToastDigestDailyTime = dto.ToastDigestDailyTime;
                config.Notifications.ToastDigestWeeklyTime = dto.ToastDigestWeeklyTime;
                config.Notifications.ToastDigestWeeklyDay = Enum.Parse<DayOfWeek>(dto.ToastDigestWeeklyDay);
                // ToastLastDailyDigestSentDate/ToastLastWeeklyDigestSentDate deliberately untouched -
                // scheduler-internal bookkeeping, not part of this DTO at all.
                config.Notifications.MessageStyle = Enum.Parse<NotificationMessageStyle>(dto.MessageStyle);
                config.Notifications.CustomTitleTemplate = dto.CustomTitleTemplate;
                config.Notifications.CustomBodyTemplate = dto.CustomBodyTemplate;
                config.Notifications.IncludeMachineName = dto.IncludeMachineName;
                config.Notifications.MachineName = MachineLabel.Clean(dto.MachineName);
                return true;
            });
            return Results.Ok();
        });

        // This computer's own name, offered as a starting point when the user ticks "Include machine
        // name" - never sent anywhere or saved unless they keep it and press Save.
        app.MapGet("/api/notifications/machine-name-suggestion", () =>
            Results.Json(new { name = MachineLabel.Clean(Environment.MachineName) }));

        // The built-in styles' actual Title/Body wording, so the Notifications page can preview
        // them without hardcoding the same text a second time in notifications.js - this endpoint
        // is the single source of truth for what "Minimal"/"Standard"/"Detailed" actually say.
        app.MapGet("/api/notifications/message-presets", () =>
        {
            var dto = NotificationMessagePresets.Templates.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new MessagePresetDto(kv.Value.Title, kv.Value.Body));
            return Results.Json(dto);
        });

        app.MapGet("/api/notifications/types", (IEnumerable<INotifier> notifiers) =>
        {
            var types = notifiers.Select(n => new NotifierTypeDto(
                n.Type,
                n.DisplayName,
                n.Fields.Select(f => new NotifierFieldDto(f.Key, f.Label, f.InputType, f.Required, f.Secret, f.Options, f.HelpText, f.Placeholder)).ToList()))
                .ToList();
            return Results.Json(types);
        });

        app.MapGet("/api/notifications/channels", (IConfigStore configStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(config.Notifications.Channels.Select(ConfigMapping.ToChannelDto).ToList());
        });

        app.MapPost("/api/notifications/channels", (CreateChannelRequest request, IConfigStore configStore, IEnumerable<INotifier> notifiers) =>
        {
            var notifier = notifiers.FirstOrDefault(n => string.Equals(n.Type, request.Type, StringComparison.OrdinalIgnoreCase));
            if (notifier is null) return Results.BadRequest(new { message = $"Unknown notification type '{request.Type}'." });

            var dto = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                var channel = new NotificationChannel
                {
                    Type = notifier.Type,
                    DisplayName = notifier.DisplayName
                };
                // Seed real shared defaults (e.g. ntfy's public ntfy.sh server) - most fields have
                // none, since most fields (a Discord webhook URL, a self-hosted server) are
                // inherently per-user with nothing valid to pre-fill.
                foreach (var field in notifier.Fields)
                {
                    if (field.DefaultValue is not null) channel.Settings[field.Key] = field.DefaultValue;
                }
                config.Notifications.Channels.Add(channel);
                return ConfigMapping.ToChannelDto(channel);
            });

            return Results.Json(dto);
        });

        app.MapPut("/api/notifications/channels/{id}", (string id, NotificationChannelDto dto, IConfigStore configStore) =>
        {
            var result = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                var channel = config.Notifications.Channels.FirstOrDefault(c => c.Id == id);
                if (channel is null) return null;

                ConfigMapping.ApplyChannelDto(channel, dto);
                return ConfigMapping.ToChannelDto(channel);
            });

            return result is null ? Results.NotFound() : Results.Json(result);
        });

        app.MapDelete("/api/notifications/channels/{id}", (string id, IConfigStore configStore) =>
        {
            var removed = configStore.Update(AppPaths.GetConfigFilePath(), config => config.Notifications.Channels.RemoveAll(c => c.Id == id));
            return removed == 0 ? Results.NotFound() : Results.NoContent();
        });

        // Tests whatever's currently typed into a channel's fields, saved or not - same "test the
        // form, not the file" behavior /api/arr/test already has for Sonarr/Radarr.
        app.MapPost("/api/notifications/test", async (TestNotifierRequest request, IEnumerable<INotifier> notifiers, IConfigStore configStore) =>
        {
            var notifier = notifiers.FirstOrDefault(n => string.Equals(n.Type, request.Type, StringComparison.OrdinalIgnoreCase));
            if (notifier is null) return Results.Json(new { success = false, message = $"Unknown notification type '{request.Type}'." });

            // The saved machine-name choice, so the test message previews what a real one will look like.
            var machineName = MachineLabel.For(configStore.Load(AppPaths.GetConfigFilePath()).Notifications);
            var result = await notifier.TestAsync(request.Settings, CancellationToken.None, machineName);
            return Results.Json(new { success = result.Success, message = result.Message });
        });

        // Fires a real digest immediately, bypassing the schedule entirely - built from actual
        // History (the real Daily/Weekly window a scheduled send would use), so this proves both
        // the channel's delivery AND the real current numbers, not a canned test message. Same
        // "test what's on screen, not what's saved" behavior as /api/notifications/test above.
        // request.Weekly picks which window/label to use - the client calls this once per digest
        // type currently checked on the card, so testing with both Daily and Weekly enabled sends
        // two distinctly-labeled tests instead of two identical "Daily Digest" ones.
        app.MapPost("/api/notifications/digest-test", async (DigestTestRequest request, IEnumerable<INotifier> notifiers, IConfigStore configStore, IRunHistoryStore historyStore, IPathExpander pathExpander) =>
        {
            var notifier = notifiers.FirstOrDefault(n => string.Equals(n.Type, request.Type, StringComparison.OrdinalIgnoreCase));
            if (notifier is null) return Results.Json(new { success = false, message = $"Unknown notification type '{request.Type}'." });

            var periodLabel = request.Weekly ? "Weekly Digest" : "Daily Digest";
            var summary = DigestTestSummary(configStore, historyStore, pathExpander, request.Weekly);
            var evt = summary.ToNotificationEvent(periodLabel, MachineLabel.For(configStore.Load(AppPaths.GetConfigFilePath()).Notifications));
            var result = await notifier.SendAsync(request.Settings, evt, CancellationToken.None);
            return Results.Json(new { success = result.Success, message = $"{periodLabel}: {result.Message}" });
        });

        app.MapPost("/api/notifications/digest-test/toast", (DigestTestToastRequest request, IConfigStore configStore, IRunHistoryStore historyStore, IPathExpander pathExpander, INotificationService notifications) =>
        {
            var periodLabel = request.Weekly ? "Weekly Digest" : "Daily Digest";
            var summary = DigestTestSummary(configStore, historyStore, pathExpander, request.Weekly);
            // A toast needs a real launch target to actually show up on an unpackaged Win32 app -
            // see IDigestScheduler's own note on this, confirmed live 2026-09-07.
            var port = configStore.Load(AppPaths.GetConfigFilePath()).Web.Port;
            notifications.NotifyDigestComplete(summary, periodLabel, $"http://localhost:{port}/history.html");
            // NotifyDigestComplete is best-effort/void (see its own doc comment) - there's no
            // success/failure signal to report beyond "the call was made."
            return Results.Json(new { success = true, message = $"{periodLabel} sent." });
        });
    }

    private static DigestSummary DigestTestSummary(IConfigStore configStore, IRunHistoryStore historyStore, IPathExpander pathExpander, bool weekly)
    {
        var config = configStore.Load(AppPaths.GetConfigFilePath());
        var logFilePath = pathExpander.Expand(config.Logging.LogFilePath);
        var history = historyStore.GetHistory(logFilePath);
        var today = DateOnly.FromDateTime(DateTime.Now);
        return weekly ? DigestSummaryBuilder.BuildWeekly(history, today) : DigestSummaryBuilder.BuildDaily(history, today);
    }
}
