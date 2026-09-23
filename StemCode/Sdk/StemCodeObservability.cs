namespace StemCode.Sdk;

/// <summary>
/// Shared observability names for SDK consumers.
/// </summary>
public static class StemCodeObservability
{
    /// <summary>
    /// ActivitySource name used by <see cref="StemCodeClientBuilder.WithOpenTelemetryTracing"/>.
    /// Add this source to an OpenTelemetry tracer provider to export SDK spans.
    /// </summary>
    public const string ActivitySourceName = "StemCode.Sdk";
}
