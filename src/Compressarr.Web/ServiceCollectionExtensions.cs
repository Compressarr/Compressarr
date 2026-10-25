using Compressarr.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers Web-only services. Must be called after AddCompressarrCore - the
    /// CurrentRunStateService registration below overrides Core's NullRunProgressReporter
    /// default via normal last-registered-wins DI resolution.</summary>
    public static IServiceCollection AddCompressarrWeb(this IServiceCollection services)
    {
        services.AddSingleton<CurrentRunStateService>();
        services.AddSingleton<Compressarr.Web.Api.ApiKeyThrottle>();
        services.AddSingleton<Compressarr.Web.Auth.WebAuthGate>();
        services.AddSingleton<IRunProgressReporter>(sp => sp.GetRequiredService<CurrentRunStateService>());

        return services;
    }
}
