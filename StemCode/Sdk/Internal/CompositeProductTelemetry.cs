using StemCode.Application.Abstractions;
using StemCode.Application.Models;

namespace StemCode.Sdk.Internal;

internal sealed class CompositeProductTelemetry(
    IProductTelemetry first,
    IProductTelemetry second) : IProductTelemetry
{
    public void TrackAppStarted()
    {
        Track(static telemetry => telemetry.TrackAppStarted());
    }

    public void TrackAppStopped()
    {
        Track(static telemetry => telemetry.TrackAppStopped());
    }

    public void TrackFeatureUsed(
        string featureName,
        string interactionKind,
        bool success,
        ConversationTurnMetrics? metrics = null,
        int attachmentCount = 0,
        Exception? exception = null)
    {
        Track(telemetry => telemetry.TrackFeatureUsed(
            featureName,
            interactionKind,
            success,
            metrics,
            attachmentCount,
            exception));
    }

    public void TrackToolInvoked(
        string toolName,
        ToolResultStatus status,
        bool success,
        TimeSpan duration,
        ConversationExecutionPhase executionPhase,
        string? modelId = null,
        string? providerName = null,
        string? errorMessage = null)
    {
        Track(telemetry => telemetry.TrackToolInvoked(
            toolName,
            status,
            success,
            duration,
            executionPhase,
            modelId,
            providerName,
            errorMessage));
    }

    public void TrackProviderRequest(
        string providerName,
        bool success,
        TimeSpan latency,
        bool streamed,
        TimeSpan streamLatency,
        int retryCount,
        string? errorMessage = null)
    {
        Track(telemetry => telemetry.TrackProviderRequest(
            providerName,
            success,
            latency,
            streamed,
            streamLatency,
            retryCount,
            errorMessage));
    }

    private void Track(Action<IProductTelemetry> track)
    {
        TrackOne(first, track);
        TrackOne(second, track);
    }

    private static void TrackOne(
        IProductTelemetry telemetry,
        Action<IProductTelemetry> track)
    {
        try
        {
            track(telemetry);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Telemetry must never affect SDK behavior.
        }
    }
}
