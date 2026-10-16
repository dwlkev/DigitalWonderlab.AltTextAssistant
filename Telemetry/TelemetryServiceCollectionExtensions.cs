// Shared DWL package telemetry client. CANONICAL COPY lives in umbraco-plugins/DigitalWonderlab.UmbracoTelemetry;
// packages carry a vendored copy (sync-telemetry.sh). Do not edit the vendored copies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Extensions;

namespace DigitalWonderlab.Telemetry;

internal static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the heartbeat job (once per assembly) plus one product contributor. Call once per
    /// product from the package's composer.
    /// </summary>
    public static IServiceCollection AddDwlTelemetryProduct<TContributor>(this IServiceCollection services)
        where TContributor : class, ITelemetryContributor
    {
        if (!services.Any(d => d.ServiceType == typeof(TelemetryPayloadBuilder)))
        {
            services.AddHttpClient(nameof(TelemetryHeartbeatJob));
            services.TryAddSingleton<TelemetryCounters>();
            services.TryAddSingleton<TelemetryPayloadBuilder>();
            services.AddRecurringBackgroundJob<TelemetryHeartbeatJob>();
        }

        services.AddSingleton<ITelemetryContributor, TContributor>();
        return services;
    }
}
