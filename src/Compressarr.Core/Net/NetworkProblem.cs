using System.Net;
using System.Net.Sockets;

namespace Compressarr.Core.Net;

/// <summary>
/// Turns what a failed web request throws into one plain sentence a person can act on. The raw text .NET gives ("No such host is known.
/// (api.github.com:443)") means nothing to most people and was shown on every "Check for Updates" button; this says what happened in
/// ordinary words, the same way everywhere (the About page, the preset catalog, the Encoder page's downloads).
/// </summary>
public static class NetworkProblem
{
    public static string Describe(Exception exception, string service = "GitHub")
    {
        switch (exception)
        {
            case TaskCanceledException or TimeoutException:
                return $"{service} didn't answer in time. Check your internet connection and try again.";

            case HttpRequestException http:
                if (http.StatusCode is { } status) return DescribeStatus((int)status, service);

                // No status means the request never got an answer: no network, no DNS, a blocked or dropped connection. All of those
                // read the same to the person looking at the screen.
                return $"Couldn't reach {service}. Check your internet connection and try again.";

            case SocketException:
                return $"Couldn't reach {service}. Check your internet connection and try again.";

            default:
                return $"Something went wrong while contacting {service}: {exception.Message}";
        }
    }

    /// <summary>The sentence for an HTTP status that came back as an error (rate limit, missing, server trouble).</summary>
    public static string DescribeStatus(int status, string service = "GitHub") => status switch
    {
        403 or 429 => $"{service} is limiting requests from this computer right now. Try again in a few minutes.",
        404 => $"{service} doesn't have what was asked for (HTTP 404).",
        >= 500 => $"{service} had a problem answering (HTTP {status}). Try again later.",
        _ => $"{service} answered with an error (HTTP {status}). Try again later."
    };

    /// <summary>True for the exceptions Describe turns into a message (so callers can catch exactly these and no more).</summary>
    public static bool IsNetworkFailure(Exception exception) =>
        exception is HttpRequestException or SocketException or TaskCanceledException or TimeoutException;
}
