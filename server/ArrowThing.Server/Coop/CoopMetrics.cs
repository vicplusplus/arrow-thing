using System.Diagnostics.Metrics;

namespace ArrowThing.Server.Coop;

/// <summary>
/// OpenTelemetry counters + histograms for the co-op subsystem. Registered as
/// a singleton and exported via the Prometheus scrape endpoint; the Meter name
/// (<c>ArrowThing.Coop</c>) is added to the OTel metrics pipeline in
/// <c>Program.cs</c>.
///
/// Metrics cover the co-op hot paths:
/// <list type="bullet">
/// <item>Lobby lifecycle — creates / retries / generation wall time.</item>
/// <item>WebSocket connect / disconnect volume.</item>
/// <item>Clear-attempt outcomes — accepted vs. rejected, broken down by reason.</item>
/// </list>
/// Prefer structured tags (<c>reason=...</c>) over separate counters so
/// Prometheus queries can aggregate or slice without a cardinality explosion.
/// </summary>
public class CoopMetrics
{
    public const string MeterName = "ArrowThing.Coop";

    public Counter<long> LobbiesCreated { get; }
    public Counter<long> GenerationRetries { get; }
    public Histogram<double> GenerationDurationSeconds { get; }

    public Counter<long> WsConnects { get; }
    public Counter<long> WsDisconnects { get; }

    public Counter<long> ClearsAccepted { get; }

    /// <summary>
    /// Incremented on every rejected clear_attempt. Tag <c>reason</c> carries
    /// the closed-set cause: <c>dep</c>, <c>race</c>, <c>oob</c>, <c>rate</c>.
    /// </summary>
    public Counter<long> ClearsRejected { get; }

    public CoopMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        LobbiesCreated = meter.CreateCounter<long>("coop.lobbies.created");
        GenerationRetries = meter.CreateCounter<long>("coop.lobbies.gen_retries");
        GenerationDurationSeconds = meter.CreateHistogram<double>(
            "coop.lobbies.gen_duration",
            unit: "s"
        );

        WsConnects = meter.CreateCounter<long>("coop.ws.connects");
        WsDisconnects = meter.CreateCounter<long>("coop.ws.disconnects");

        ClearsAccepted = meter.CreateCounter<long>("coop.clears.accepted");
        ClearsRejected = meter.CreateCounter<long>("coop.clears.rejected");
    }
}
