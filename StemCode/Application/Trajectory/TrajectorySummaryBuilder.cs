using StemCode.Application.Models;

namespace StemCode.Application.Trajectory;

public static class TrajectorySummaryBuilder
{
    public static TrajectorySummary Build(IReadOnlyList<SessionEventRecord> events, int timelineWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(events);

        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        foreach (SessionEventRecord record in events)
        {
            DateTimeOffset timestamp = record.TimestampUtc;
            firstTimestamp = firstTimestamp is null || timestamp < firstTimestamp.Value
                ? timestamp
                : firstTimestamp;
            lastTimestamp = lastTimestamp is null || timestamp > lastTimestamp.Value
                ? timestamp
                : lastTimestamp;
        }

        string[] toolNames = events
            .Select(static record => record.ToolName)
            .Where(static toolName => !string.IsNullOrWhiteSpace(toolName))
            .Select(static toolName => toolName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static toolName => toolName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] workingDirectories = events
            .Select(static record => record.WorkingDirectory)
            .Where(static workingDirectory => !string.IsNullOrWhiteSpace(workingDirectory))
            .Select(static workingDirectory => workingDirectory!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return new TrajectorySummary(
            events.Count,
            CountEvents(events, "user_input"),
            CountEvents(events, "assistant_reasoning"),
            CountEvents(events, "assistant_output"),
            CountEvents(events, "assistant_tool_call_request"),
            CountEvents(events, "tool_call_response"),
            CountEvents(events, "execution_plan"),
            CountEvents(events, "turn_failed"),
            firstTimestamp,
            lastTimestamp,
            toolNames,
            workingDirectories,
            BuildTimeline(events, timelineWidth));
    }

    public static int CountEvents(IReadOnlyList<SessionEventRecord> events, string eventType)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        return events.Count(record => string.Equals(record.EventType, eventType, StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildTimeline(IReadOnlyList<SessionEventRecord> events, int maxWidth = 80)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0 || maxWidth <= 0)
        {
            return string.Empty;
        }

        if (events.Count <= maxWidth)
        {
            return new string(events.Select(GetTimelineMarker).ToArray());
        }

        char[] markers = new char[maxWidth];
        for (int index = 0; index < maxWidth; index++)
        {
            int eventIndex = (int)Math.Floor(index * (events.Count / (double)maxWidth));
            markers[index] = GetTimelineMarker(events[Math.Min(eventIndex, events.Count - 1)]);
        }

        return new string(markers);
    }

    public static char GetTimelineMarker(SessionEventRecord record)
    {
        string eventType = record.EventType ?? string.Empty;
        if (eventType.Contains("user", StringComparison.OrdinalIgnoreCase))
        {
            return 'U';
        }

        if (eventType.Contains("retry", StringComparison.OrdinalIgnoreCase))
        {
            return 'R';
        }

        if (eventType.Contains("chunk", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("delta", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("first_token", StringComparison.OrdinalIgnoreCase))
        {
            return 'C';
        }

        if (eventType.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("prompt", StringComparison.OrdinalIgnoreCase))
        {
            return 'M';
        }

        if (eventType.Contains("tool", StringComparison.OrdinalIgnoreCase))
        {
            return 'T';
        }

        if (eventType.Contains("assistant", StringComparison.OrdinalIgnoreCase))
        {
            return 'A';
        }

        if (eventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            eventType.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return 'E';
        }

        if (eventType.Contains("plan", StringComparison.OrdinalIgnoreCase))
        {
            return 'P';
        }

        return '.';
    }
}
