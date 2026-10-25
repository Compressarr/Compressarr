namespace Compressarr.Core.Reporting;

/// <summary>Where a report built from the run archive can be opened: the app's own /api/reports/{id}.</summary>
public static class ReportLinks
{
    /// <summary>For this machine (the report that opens after a run, a toast's click).</summary>
    public static string LocalUrl(int port, long runId) => $"http://localhost:{port}/api/reports/{runId}";

    /// <summary>For other devices (a notification sent to a phone): the machine's name instead of localhost. The web UI listens on every interface.</summary>
    public static string RemoteUrl(int port, long runId) => $"http://{Environment.MachineName.ToLowerInvariant()}:{port}/api/reports/{runId}";
}
