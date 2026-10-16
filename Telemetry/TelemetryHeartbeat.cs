// Shared DWL package telemetry client. CANONICAL COPY lives in umbraco-plugins/DigitalWonderlab.UmbracoTelemetry;
// packages carry a vendored copy (sync-telemetry.sh). Do not edit the vendored copies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Configuration;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using IUmbracoHostingEnvironment = Umbraco.Cms.Core.Hosting.IHostingEnvironment;

namespace DigitalWonderlab.Telemetry;

/// <summary>
/// Builds the heartbeat payload. Also used by product settings screens to show exactly what is sent.
/// Nothing identifying goes in: no hostnames, URLs, content, names, emails or IPs.
/// </summary>
internal sealed class TelemetryPayloadBuilder
{
    internal const string DefaultEndpoint = "https://dwl-umbraco-telemetry-api-h5h4axfnfua8atbd.uksouth-01.azurewebsites.net/api/v2/heartbeat";
    private const string KeyPrefix = "DigitalWonderlab.Telemetry.";

    private readonly IKeyValueService _keyValues;
    private readonly IUmbracoVersion _umbracoVersion;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly IUmbracoHostingEnvironment _umbracoHosting;
    private readonly IConfiguration _configuration;

    public TelemetryPayloadBuilder(IKeyValueService keyValues, IUmbracoVersion umbracoVersion, IHostEnvironment hostEnvironment,
        IUmbracoHostingEnvironment umbracoHosting, IConfiguration configuration)
    {
        _keyValues = keyValues;
        _umbracoVersion = umbracoVersion;
        _hostEnvironment = hostEnvironment;
        _umbracoHosting = umbracoHosting;
        _configuration = configuration;
    }

    public string Endpoint => _configuration["DigitalWonderlab:Telemetry:Endpoint"] is { Length: > 0 } e ? e : DefaultEndpoint;

    /// <summary>Global kill switch for every DWL package on the site.</summary>
    public bool GloballyEnabled => _configuration.GetValue<bool?>("DigitalWonderlab:Telemetry:Enabled") != false;

    /// <summary>Local development machines are skipped unless explicitly included (keeps our own dev boxes out).</summary>
    public bool EnvironmentAllowed => !_hostEnvironment.IsDevelopment()
        || _configuration.GetValue<bool>("DigitalWonderlab:Telemetry:IncludeDevelopment");

    public Guid GetOrCreateInstallId(string productId)
    {
        var key = KeyPrefix + productId + ".InstallId";
        if (Guid.TryParse(_keyValues.GetValue(key), out var existing) && existing != Guid.Empty)
            return existing;

        var id = Guid.NewGuid();
        _keyValues.SetValue(key, id.ToString());
        _keyValues.SetValue(KeyPrefix + productId + ".CreatedUtc", DateTime.UtcNow.ToString("O"));
        return id;
    }

    /// <summary>
    /// Backoffice toggle for packages with no settings store of their own (dashboards): kept in
    /// umbracoKeyValue next to the install id. On unless explicitly switched off.
    /// </summary>
    public bool IsProductToggleOn(string productId)
        => !string.Equals(_keyValues.GetValue(KeyPrefix + productId + ".Enabled"), "false", StringComparison.OrdinalIgnoreCase);

    public void SetProductToggle(string productId, bool enabled)
        => _keyValues.SetValue(KeyPrefix + productId + ".Enabled", enabled ? "true" : "false");

    public DateTime? GetLastSentUtc(string productId)
        => DateTime.TryParse(_keyValues.GetValue(KeyPrefix + productId + ".LastSentUtc"), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;

    public void SetLastSentUtc(string productId, DateTime value)
        => _keyValues.SetValue(KeyPrefix + productId + ".LastSentUtc", value.ToString("O"));

    public TelemetryPayload Build(ITelemetryContributor contributor)
    {
        var report = new TelemetryReport();
        contributor.Contribute(report);

        var installId = GetOrCreateInstallId(contributor.ProductId);
        int? firstSeenDays = DateTime.TryParse(_keyValues.GetValue(KeyPrefix + contributor.ProductId + ".CreatedUtc"), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var created)
            ? Math.Max(0, (int)(DateTime.UtcNow - created).TotalDays) : null;

        return new TelemetryPayload
        {
            Product = contributor.ProductId,
            InstallId = installId,
            PackageVersion = contributor.PackageVersion,
            UmbracoVersion = _umbracoVersion.Version.ToString(3),
            DotnetVersion = Environment.Version.ToString(),
            Environment = ClassifyEnvironment(),
            Hosting = DetectHosting(),
            DatabaseProvider = DetectDatabaseProvider(),
            FirstSeenDays = firstSeenDays,
            SizeBand = report.SizeBand,
            Features = report.Features,
            Counters = report.Counters
        };
    }

    public static string? VersionOf(Assembly assembly)
        => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
           ?? assembly.GetName().Version?.ToString(3);

    /// <summary>
    /// Umbraco Cloud runs every environment as Production, so dev/stage are told apart by their
    /// *.umbraco.io hostname prefix. Only the classification is sent, never the host.
    /// </summary>
    private string ClassifyEnvironment()
    {
        var host = _umbracoHosting.ApplicationMainUrl?.Host?.ToLowerInvariant() ?? string.Empty;
        if (host.EndsWith(".umbraco.io"))
        {
            if (host.StartsWith("dev-")) return "Development";
            if (host.StartsWith("stage-") || host.StartsWith("staging-")) return "Staging";
        }

        if (_hostEnvironment.IsProduction()) return "Production";
        if (_hostEnvironment.IsStaging()) return "Staging";
        if (_hostEnvironment.IsDevelopment()) return "Development";
        return "Other";
    }

    private static string DetectHosting()
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("Umbraco.Cloud.", StringComparison.Ordinal) == true))
            return "umbraco-cloud";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME")))
            return "azure-app-service";
        return "other";
    }

    private string DetectDatabaseProvider()
    {
        var provider = _configuration["ConnectionStrings:umbracoDbDSN_ProviderName"] ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) return "sqlite";
        if (provider.Contains("SqlClient", StringComparison.OrdinalIgnoreCase)) return "sqlserver";
        return "other";
    }
}

/// <summary>Wire format of a v2 heartbeat (serialised camelCase).</summary>
internal sealed class TelemetryPayload
{
    public int Schema { get; init; } = 1;
    public required string Product { get; init; }
    public Guid InstallId { get; init; }
    public string? PackageVersion { get; init; }
    public string? UmbracoVersion { get; init; }
    public string? DotnetVersion { get; init; }
    public required string Environment { get; init; }
    public required string Hosting { get; init; }
    public required string DatabaseProvider { get; init; }
    public int? FirstSeenDays { get; init; }
    public Dictionary<string, string> SizeBand { get; init; } = new();
    public Dictionary<string, bool> Features { get; init; } = new();
    public Dictionary<string, string> Counters { get; init; } = new();
}

/// <summary>
/// Checks hourly and sends each enabled product's heartbeat at most once per ~day. Runs only on the
/// single / scheduling-publisher server, so load-balanced sites do not double count. Last-sent time
/// is persisted, so restarts and Cloud deploys do not cause extra heartbeats.
/// </summary>
internal sealed class TelemetryHeartbeatJob : IRecurringBackgroundJob
{
    private static readonly TimeSpan SendInterval = TimeSpan.FromHours(23);

    private readonly IEnumerable<ITelemetryContributor> _contributors;
    private readonly TelemetryPayloadBuilder _builder;
    private readonly TelemetryCounters _counters;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TelemetryHeartbeatJob> _logger;

    public TelemetryHeartbeatJob(IEnumerable<ITelemetryContributor> contributors, TelemetryPayloadBuilder builder,
        TelemetryCounters counters, IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TelemetryHeartbeatJob> logger)
    {
        // Jittered so a fleet of sites restarting together does not all call in at once.
        // FirstRunDelaySeconds exists for testing only.
        var configured = configuration.GetValue<int?>("DigitalWonderlab:Telemetry:FirstRunDelaySeconds");
        Delay = configured is >= 0 ? TimeSpan.FromSeconds(configured.Value) : TimeSpan.FromMinutes(5 + Random.Shared.Next(0, 25));
        _contributors = contributors;
        _builder = builder;
        _counters = counters;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public TimeSpan Period => TimeSpan.FromHours(1);

    public TimeSpan Delay { get; }

    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public event EventHandler PeriodChanged { add { } remove { } }

    public async Task RunJobAsync()
    {
        if (!_builder.GloballyEnabled || !_builder.EnvironmentAllowed)
            return;

        foreach (var contributor in _contributors)
        {
            try
            {
                if (!contributor.IsEnabled())
                    continue;

                var last = _builder.GetLastSentUtc(contributor.ProductId);
                if (last.HasValue && DateTime.UtcNow - last.Value < SendInterval)
                    continue;

                var payload = _builder.Build(contributor);
                var client = _httpClientFactory.CreateClient(nameof(TelemetryHeartbeatJob));
                // Generous: the API is on a plan that cold-starts. This runs in the background, nothing waits on it.
                client.Timeout = TimeSpan.FromSeconds(30);

                using var response = await client.PostAsJsonAsync(_builder.Endpoint, payload);
                if (response.IsSuccessStatusCode)
                {
                    _builder.SetLastSentUtc(contributor.ProductId, DateTime.UtcNow);
                    _counters.Reset(payload.Counters.Keys.ToArray());
                    contributor.OnSent();
                }
                else
                {
                    _logger.LogDebug("Telemetry heartbeat for {Product} returned {Status}", contributor.ProductId, (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Telemetry heartbeat for {Product} failed", contributor.ProductId);
            }
        }
    }
}
