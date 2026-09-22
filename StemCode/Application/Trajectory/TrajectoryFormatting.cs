using StemCode.Application.Models;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StemCode.Application.Trajectory;

public static class TrajectoryFormatting
{
    public static string FormatEventType(string eventType)
    {
        return string.Join(
            ' ',
            (eventType ?? string.Empty).Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    public static string FormatTimestamp(DateTimeOffset? value)
    {
        return value is null
            ? string.Empty
            : value.Value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
    }

    public static string FormatTiming(
        DateTimeOffset? timestampUtc,
        DateTimeOffset? previousTimestampUtc,
        DateTimeOffset? turnStartedAtUtc)
    {
        if (timestampUtc is null)
        {
            return string.Empty;
        }

        List<string> parts = [];
        if (previousTimestampUtc is not null)
        {
            parts.Add("+" + FormatDuration(timestampUtc.Value - previousTimestampUtc.Value));
        }

        if (turnStartedAtUtc is not null)
        {
            parts.Add("turn+" + FormatDuration(timestampUtc.Value - turnStartedAtUtc.Value));
        }

        return string.Join(" ", parts);
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalMilliseconds < 1000)
        {
            return ((int)Math.Round(duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + "ms";
        }

        if (duration.TotalMinutes < 1)
        {
            return duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        if (duration.TotalHours < 1)
        {
            return ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " +
                duration.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
        }

        return ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " +
            duration.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    public static string? PrettyJsonOrRaw(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
            {
                document.RootElement.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return value;
        }
    }

    public static string? FormatUsage(SessionEventRecord record)
    {
        List<string> parts = [];
        AddNumber(parts, "input", record.InputTokens);
        AddNumber(parts, "cacheRead", record.CacheReadTokens);
        AddNumber(parts, "cacheWrite", record.CacheWriteTokens);
        AddNumber(parts, "output", record.OutputTokens);
        AddNumber(parts, "reasoning", record.ReasoningTokens);
        AddNumber(parts, "itemsReplaced", record.ItemsReplaced);
        AddNumber(parts, "tokensReplaced", record.TokensReplaced);
        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    public static string? FormatTimingBlock(SessionEventRecord record)
    {
        List<string> parts = [];
        AddText(parts, "requestStart", FormatTimestamp(record.RequestStartedAtUtc));
        AddText(parts, "firstToken", FormatTimestamp(record.FirstTokenAtUtc));
        AddText(parts, "completed", FormatTimestamp(record.CompletedAtUtc));
        AddDurationMs(parts, "total", record.TotalDurationMs);
        AddDurationMs(parts, "TTFT", record.TtftMs);
        AddDurationMs(parts, "generation", record.GenerationDurationMs);
        if (record.TokensPerSecond is not null)
        {
            parts.Add("tokens/sec=" + record.TokensPerSecond.Value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    public static string? FormatErrors(SessionEventRecord record)
    {
        List<string> parts = [];
        AddText(parts, "type", record.ErrorType);
        AddText(parts, "code", record.ErrorCode);
        AddNumber(parts, "retry", record.RetryNumber);
        AddNumber(parts, "maxRetries", record.MaxRetries);
        AddNumber(parts, "delayMs", record.RetryDelayMs);
        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    public static string CreatePreview(string value, int maxLength = 72)
    {
        string normalized = string.Join(
            ' ',
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
    }

    private static void AddNumber(List<string> parts, string label, int? value)
    {
        if (value is not null)
        {
            parts.Add(label + "=" + value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddDurationMs(List<string> parts, string label, double? value)
    {
        if (value is not null)
        {
            parts.Add(label + "=" + FormatDuration(TimeSpan.FromMilliseconds(value.Value)));
        }
    }

    private static void AddText(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(label + "=" + value.Trim());
        }
    }
}
