using StemCode.Application.Models;
using System.Globalization;
using System.Text.Json;

namespace StemCode.Application.Trajectory;

public static class TrajectoryLogReader
{
    public static IReadOnlyList<SessionEventRecord> LoadFromFile(string eventLogPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventLogPath);

        if (!File.Exists(eventLogPath))
        {
            return [];
        }

        List<SessionEventRecord> events = [];
        foreach (string rawLine in File.ReadLines(eventLogPath))
        {
            if (TryParseLine(rawLine, out SessionEventRecord record))
            {
                events.Add(record);
            }
        }

        return events;
    }

    public static async Task<IReadOnlyList<SessionEventRecord>> LoadFromFileAsync(
        string eventLogPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventLogPath);

        if (!File.Exists(eventLogPath))
        {
            return [];
        }

        List<SessionEventRecord> events = [];
        await foreach (string line in File.ReadLinesAsync(eventLogPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryParseLine(line, out SessionEventRecord record))
            {
                events.Add(record);
            }
        }

        return events
            .OrderBy(static record => record.EventSequenceId ?? long.MaxValue)
            .ThenBy(static record => record.TimestampUtc)
            .ToArray();
    }

    public static bool TryParseLine(string json, out SessionEventRecord record)
    {
        record = null!;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            record = new SessionEventRecord(
                TryGetDateTimeOffset(root, "timestampUtc") ?? default,
                TryGetString(root, "sectionId") ?? string.Empty,
                TryGetString(root, "parentSessionId"),
                TryGetString(root, "eventType") ?? "event",
                TryGetString(root, "agentProfileName") ?? string.Empty,
                TryGetString(root, "modelId") ?? string.Empty,
                TryGetString(root, "workingDirectory") ?? string.Empty,
                TryGetString(root, "text"),
                TryGetString(root, "toolCallId"),
                TryGetString(root, "toolName"),
                TryGetString(root, "toolArgumentsJson"),
                TryGetString(root, "toolStatus"),
                TryGetString(root, "toolMessage"),
                TryGetString(root, "toolResultJson"),
                TryGetString(root, "errorType"),
                TryGetInt64(root, "eventSequenceId"),
                TryGetString(root, "turnId"),
                TryGetInt32(root, "turnIndex"),
                TryGetString(root, "stepId"),
                TryGetInt32(root, "stepIndex"),
                TryGetString(root, "modelRequestId"),
                TryGetString(root, "assistantMessageId"),
                TryGetString(root, "toolResultId"),
                TryGetString(root, "parentCallId"),
                TryGetString(root, "source"),
                TryGetString(root, "systemPrompt"),
                TryGetString(root, "toolsJson"),
                TryGetString(root, "toolSchemaJson"),
                TryGetString(root, "requestOptionsJson"),
                TryGetString(root, "inputJson"),
                TryGetString(root, "outputJson"),
                TryGetString(root, "thinkingJson"),
                TryGetString(root, "rawJson"),
                TryGetString(root, "status"),
                TryGetString(root, "errorCode"),
                TryGetInt32(root, "retryNumber"),
                TryGetInt32(root, "maxRetries"),
                TryGetInt32(root, "retryDelayMs"),
                TryGetInt32(root, "inputTokens"),
                TryGetInt32(root, "cacheReadTokens"),
                TryGetInt32(root, "cacheWriteTokens"),
                TryGetInt32(root, "outputTokens"),
                TryGetInt32(root, "reasoningTokens"),
                TryGetDateTimeOffset(root, "requestStartedAtUtc"),
                TryGetDateTimeOffset(root, "firstTokenAtUtc"),
                TryGetDateTimeOffset(root, "completedAtUtc"),
                TryGetDouble(root, "totalDurationMs"),
                TryGetDouble(root, "ttftMs"),
                TryGetDouble(root, "generationDurationMs"),
                TryGetDouble(root, "tokensPerSecond"),
                TryGetBool(root, "isPartial"),
                TryGetBool(root, "isRunning"),
                TryGetBool(root, "interrupted"),
                TryGetString(root, "summary"),
                TryGetInt32(root, "itemsReplaced"),
                TryGetInt32(root, "tokensReplaced"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, string propertyName)
    {
        string? value = TryGetString(root, propertyName);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
                ? parsed
                : null;
    }

    private static int? TryGetInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return int.TryParse(TryGetString(root, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static long? TryGetInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        return long.TryParse(TryGetString(root, propertyName), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : null;
    }

    private static double? TryGetDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        return double.TryParse(TryGetString(root, propertyName), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static bool? TryGetBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(TryGetString(root, propertyName), out bool parsed) ? parsed : null
        };
    }
}
