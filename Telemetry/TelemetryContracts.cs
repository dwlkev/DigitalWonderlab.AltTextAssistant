// Shared DWL package telemetry client. CANONICAL COPY lives in umbraco-plugins/DigitalWonderlab.UmbracoTelemetry;
// packages carry a vendored copy (sync-telemetry.sh). Do not edit the vendored copies.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace DigitalWonderlab.Telemetry;

/// <summary>Size bands the telemetry API accepts. Exact counts are never sent.</summary>
internal static class TelemetryBand
{
    public static string Of(long value) => value switch
    {
        <= 0 => "0",
        < 10 => "1-9",
        < 100 => "10-99",
        < 1000 => "100-999",
        _ => "1000+"
    };
}

/// <summary>What one product contributes to its heartbeat. Keys must be on the API's allow-list.</summary>
internal sealed class TelemetryReport
{
    public Dictionary<string, string> SizeBand { get; } = new();
    public Dictionary<string, bool> Features { get; } = new();
    public Dictionary<string, string> Counters { get; } = new();
}

/// <summary>One per product. Registered with <see cref="TelemetryServiceCollectionExtensions.AddDwlTelemetryProduct{T}"/>.</summary>
internal interface ITelemetryContributor
{
    /// <summary>Product id on the API allow-list, e.g. <c>community-forum</c>.</summary>
    string ProductId { get; }

    /// <summary>Package version reported, normally the product assembly's informational version.</summary>
    string? PackageVersion { get; }

    /// <summary>
    /// Product-level consent on top of the global switch: config key (e.g. <c>Community:Telemetry:Enabled</c>)
    /// and/or a backoffice toggle. Return false to send nothing.
    /// </summary>
    bool IsEnabled();

    /// <summary>
    /// Fill sizes, feature flags and counters. Must not throw for routine conditions and must not
    /// change state: it is also called to preview the payload in settings screens.
    /// </summary>
    void Contribute(TelemetryReport report);

    /// <summary>Called after a heartbeat was accepted, to reset any extra per-day state.</summary>
    void OnSent() { }
}

/// <summary>
/// In-memory usage counters, reset each time a heartbeat is sent. Lost on restart by design;
/// they are only ever reported as bands.
/// </summary>
internal sealed class TelemetryCounters
{
    private readonly ConcurrentDictionary<string, long> _counts = new();

    public void Increment(string key, long by = 1) => _counts.AddOrUpdate(key, by, (_, v) => v + by);

    public long Peek(string key) => _counts.TryGetValue(key, out var v) ? v : 0;

    /// <summary>Adds every counter as a band (including zeros for <paramref name="keys"/>).</summary>
    public void AddTo(TelemetryReport report, params string[] keys)
    {
        foreach (var key in keys)
            report.Counters[key] = TelemetryBand.Of(Peek(key));
    }

    public void Reset(params string[] keys)
    {
        foreach (var key in keys)
            _counts.TryRemove(key, out _);
    }
}
