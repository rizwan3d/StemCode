using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;

namespace StemCode.Sdk.Internal;

internal sealed class SdkObservabilityProductTelemetry(
    ILogger<SdkObservabilityProductTelemetry> logger) : IProductTelemetry
{
    private static readonly ActivitySource ActivitySource = new(StemCodeObservability.ActivitySourceName);
    private Activity? _applicationActivity;

    public void TrackAppStarted()
    {
        if (_applicationActivity is null)
        {
            _applicationActivity = ActivitySource.StartActivity("stemcode.app", ActivityKind.Internal);
        }

        logger.LogInformation("StemCode SDK client started.");
    }

    public void TrackAppStopped()
    {
        logger.LogInformation("StemCode SDK client stopped.");
        _applicationActivity?.Dispose();
        _applicationActivity = null;
    }

    public void TrackFeatureUsed(
        string featureName,
        string interactionKind,
        bool success,
        ConversationTurnMetrics? metrics = null,
        int attachmentCount = 0,
        Exception? exception = null)
    {
        using Activity? activity = ActivitySource.StartActivity("stemcode.feature", ActivityKind.Internal);
        activity?.SetTag("stemcode.feature.name", featureName);
        activity?.SetTag("stemcode.interaction.kind", interactionKind);
        activity?.SetTag("stemcode.success", success);
        activity?.SetTag("stemcode.attachments.count", attachmentCount);
        activity?.SetTag("stemcode.provider.name", metrics?.ProviderName);
        activity?.SetTag("stemcode.model.id", metrics?.ModelId);
        activity?.SetTag("stemcode.estimated_input_tokens", metrics?.EstimatedInputTokens);
        activity?.SetTag("stemcode.estimated_output_tokens", metrics?.EstimatedOutputTokens);
        activity?.SetTag("stemcode.session_estimated_output_tokens", metrics?.SessionEstimatedOutputTokens);
        activity?.SetTag("stemcode.provider_retry_count", metrics?.ProviderRetryCount);
        activity?.SetTag("stemcode.tool_round_count", metrics?.ToolRoundCount);
        SetError(activity, success, exception?.Message);

        if (success)
        {
            logger.LogInformation(
                "StemCode SDK feature {FeatureName} completed via {InteractionKind}.",
                featureName,
                interactionKind);
        }
        else
        {
            logger.LogWarning(
                exception,
                "StemCode SDK feature {FeatureName} failed via {InteractionKind}.",
                featureName,
                interactionKind);
        }
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
        using Activity? activity = ActivitySource.StartActivity("stemcode.tool", ActivityKind.Internal);
        activity?.SetTag("stemcode.tool.name", toolName);
        activity?.SetTag("stemcode.tool.status", status.ToString());
        activity?.SetTag("stemcode.execution.phase", executionPhase.ToString());
        activity?.SetTag("stemcode.duration_ms", duration.TotalMilliseconds);
        activity?.SetTag("stemcode.provider.name", providerName);
        activity?.SetTag("stemcode.model.id", modelId);
        SetError(activity, success, errorMessage);

        if (success)
        {
            logger.LogInformation(
                "StemCode SDK tool {ToolName} completed with {Status} in {DurationMilliseconds} ms.",
                toolName,
                status,
                Math.Round(duration.TotalMilliseconds, MidpointRounding.AwayFromZero));
        }
        else
        {
            logger.LogWarning(
                "StemCode SDK tool {ToolName} failed with {Status}: {ErrorMessage}",
                toolName,
                status,
                errorMessage ?? "(none)");
        }
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
        using Activity? activity = ActivitySource.StartActivity("stemcode.provider_request", ActivityKind.Client);
        activity?.SetTag("stemcode.provider.name", providerName);
        activity?.SetTag("stemcode.duration_ms", latency.TotalMilliseconds);
        activity?.SetTag("stemcode.streamed", streamed);
        activity?.SetTag("stemcode.stream_latency_ms", streamLatency.TotalMilliseconds);
        activity?.SetTag("stemcode.retry_count", retryCount);
        SetError(activity, success, errorMessage);

        if (success)
        {
            logger.LogInformation(
                "StemCode SDK provider {ProviderName} request completed in {DurationMilliseconds} ms after {RetryCount} retries.",
                providerName,
                Math.Round(latency.TotalMilliseconds, MidpointRounding.AwayFromZero),
                retryCount);
        }
        else
        {
            logger.LogWarning(
                "StemCode SDK provider {ProviderName} request failed after {DurationMilliseconds} ms: {ErrorMessage}",
                providerName,
                Math.Round(latency.TotalMilliseconds, MidpointRounding.AwayFromZero),
                errorMessage ?? "(none)");
        }
    }

    private static void SetError(
        Activity? activity,
        bool success,
        string? errorMessage)
    {
        if (activity is null || success)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, errorMessage);
        activity.SetTag("error.type", "stemcode.telemetry");
        activity.SetTag("error.message", errorMessage);
    }
}
