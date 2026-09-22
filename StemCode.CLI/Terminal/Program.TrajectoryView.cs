using System.Globalization;
using System.Text;
using System.Text.Json;
using Spectre.Console;

namespace StemCode.CLI;

public static partial class Program
{
    private const string TrajectoryViewCommand = "/trajectory";
    private const string TrajectoryViewAlias = "/traj";

    private static bool TryHandleTrajectoryView(AppState state, string command)
    {
        string normalized = command.Trim();
        if (!string.Equals(normalized, TrajectoryViewCommand, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalized, TrajectoryViewAlias, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(state.SessionId))
        {
            state.AddSystemMessage("No active session is available yet.");
            return true;
        }

        string eventLogPath = ResolveTrajectoryEventLogPath(state.SessionId);
        int width = Math.Max(40, GetWindowWidth() - 1);
        IReadOnlyList<ReaderViewLine> lines = BuildTrajectoryStepReaderLines(
            state.SessionId,
            eventLogPath,
            width);

        EnterReaderView(
            state,
            lines,
            "TRAJECTORY",
            "select a step | Enter details | Up/Down PgUp/PgDn Home/End move | Esc/F5 exit",
            startAtBottom: false);
        state.ReaderViewKind = "trajectory";
        state.ReaderViewDataPath = eventLogPath;
        return true;
    }

    internal static string ResolveTrajectoryEventLogPath(string sectionId)
    {
        string normalizedSectionId = Guid.TryParse(sectionId.Trim(), out Guid parsed)
            ? parsed.ToString("D")
            : sectionId.Trim();

        return Path.Combine(
            GetStemCodeApplicationDataDirectoryPath(),
            "sessions",
            normalizedSectionId + ".events.jsonl");
    }

    internal static IReadOnlyList<ReaderViewLine> BuildTrajectoryReaderLines(
        string sectionId,
        string eventLogPath,
        int width)
    {
        int contentWidth = Math.Max(32, width);
        List<ReaderViewLine> lines = [];

        AddStyledLine(lines, "Trajectory", "[bold aqua]Trajectory[/]");
        AddStyledLine(lines, "Section: " + sectionId, "[grey]Section:[/] " + Markup.Escape(sectionId));
        AddStyledLine(lines, "Event log: " + eventLogPath, "[grey]Event log:[/] " + Markup.Escape(eventLogPath));

        if (!File.Exists(eventLogPath))
        {
            AddStyledLine(lines, string.Empty, string.Empty);
            AddWrappedStyledText(
                lines,
                "No trajectory events were recorded for this session yet.",
                contentWidth,
                "yellow");
            return lines;
        }

        List<TrajectoryEventRecord> events = LoadTrajectoryEvents(eventLogPath);
        AddTrajectorySummary(lines, BuildTrajectorySummary(events));
        AddStyledLine(lines, string.Empty, string.Empty);
        AddStyledLine(lines, "Event stream", "[bold white]Event stream[/]");
        AddStyledLine(lines, string.Empty, string.Empty);

        int eventIndex = 0;
        int turnIndex = 0;
        DateTimeOffset? previousTimestamp = null;
        DateTimeOffset? turnStartedAt = null;

        foreach (string rawLine in File.ReadLines(eventLogPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            if (!TryParseTrajectoryEvent(rawLine, out TrajectoryEventRecord record))
            {
                eventIndex++;
                AddEventHeader(lines, eventIndex, "invalid event", null, null, null, null);
                AddWrappedStyledText(lines, rawLine.Trim(), contentWidth, "red");
                AddStyledLine(lines, string.Empty, string.Empty);
                continue;
            }

            eventIndex++;
            if (string.Equals(record.EventType, "user_input", StringComparison.OrdinalIgnoreCase))
            {
                turnIndex++;
                turnStartedAt = record.TimestampUtc;
                AddTurnSeparator(lines, turnIndex, record.TimestampUtc);
            }

            string location = turnIndex == 0
                ? "Between turns"
                : "Turn " + turnIndex.ToString(CultureInfo.InvariantCulture);
            string timing = FormatTrajectoryTiming(record.TimestampUtc, previousTimestamp, turnStartedAt);
            AddEventHeader(
                lines,
                eventIndex,
                record.EventType,
                record.TimestampUtc,
                record.ToolName,
                location,
                timing);
            AddTrajectoryMetadata(lines, record, contentWidth);
            AddOptionalTrajectoryBlock(lines, "Summary", record.Summary ?? record.Text, contentWidth, "white");
            AddOptionalTrajectoryBlock(lines, "Input", PrettyJsonOrRaw(record.InputJson), contentWidth, "aqua");
            AddOptionalTrajectoryBlock(lines, "Output", PrettyJsonOrRaw(record.OutputJson) ?? record.Text, contentWidth, "green");
            AddOptionalTrajectoryBlock(lines, "Thinking", PrettyJsonOrRaw(record.ThinkingJson), contentWidth, "yellow");
            AddOptionalTrajectoryBlock(lines, "Source", record.Source, contentWidth, "white");
            AddOptionalTrajectoryBlock(lines, "System Prompt", record.SystemPrompt, contentWidth, "white");
            AddOptionalTrajectoryBlock(lines, "Tools", PrettyJsonOrRaw(record.ToolsJson), contentWidth, "aqua");
            AddOptionalTrajectoryBlock(lines, "Tool Schema", PrettyJsonOrRaw(record.ToolSchemaJson), contentWidth, "aqua");
            AddOptionalTrajectoryBlock(lines, "Request Options", PrettyJsonOrRaw(record.RequestOptionsJson), contentWidth, "aqua");
            AddOptionalTrajectoryBlock(lines, "Usage", FormatTrajectoryUsage(record), contentWidth, "magenta");
            AddOptionalTrajectoryBlock(lines, "Timing", FormatTrajectoryTimingBlock(record), contentWidth, "magenta");
            AddOptionalTrajectoryBlock(lines, "Errors", FormatTrajectoryErrors(record), contentWidth, "red");
            AddOptionalTrajectoryBlock(lines, "Tool arguments", PrettyJsonOrRaw(record.ToolArgumentsJson), contentWidth, "aqua");
            AddOptionalTrajectoryBlock(lines, "Tool message", record.ToolMessage, contentWidth, "white");
            AddOptionalTrajectoryBlock(lines, "Tool result", PrettyJsonOrRaw(record.ToolResultJson), contentWidth, "green");
            AddOptionalTrajectoryBlock(lines, "Raw", PrettyJsonOrRaw(record.RawJson), contentWidth, "grey");
            AddStyledLine(lines, string.Empty, string.Empty);
            previousTimestamp = record.TimestampUtc ?? previousTimestamp;
        }

        if (eventIndex == 0)
        {
            AddWrappedStyledText(
                lines,
                "The trajectory event log exists but is empty.",
                contentWidth,
                "yellow");
        }

        return lines;
    }

    internal static IReadOnlyList<ReaderViewLine> BuildTrajectoryStepReaderLines(
        string sectionId,
        string eventLogPath,
        int width)
    {
        int contentWidth = Math.Max(32, width);
        List<ReaderViewLine> lines = [];

        AddStyledLine(lines, "Trajectory", "[bold aqua]Trajectory[/]");
        AddStyledLine(lines, "Section: " + sectionId, "[grey]Section:[/] " + Markup.Escape(sectionId));
        AddStyledLine(lines, "Event log: " + eventLogPath, "[grey]Event log:[/] " + Markup.Escape(eventLogPath));

        if (!File.Exists(eventLogPath))
        {
            AddStyledLine(lines, string.Empty, string.Empty);
            AddWrappedStyledText(
                lines,
                "No trajectory events were recorded for this session yet.",
                contentWidth,
                "yellow");
            return lines;
        }

        List<TrajectoryEventRecord> events = LoadTrajectoryEvents(eventLogPath);
        AddTrajectorySummary(lines, BuildTrajectorySummary(events));
        AddStyledLine(lines, string.Empty, string.Empty);
        AddStyledLine(lines, "Steps", "[bold white]Steps[/]");
        AddStyledLine(lines, string.Empty, string.Empty);

        int eventIndex = 0;
        int turnIndex = 0;
        DateTimeOffset? previousTimestamp = null;
        DateTimeOffset? turnStartedAt = null;

        foreach (string rawLine in File.ReadLines(eventLogPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            eventIndex++;
            if (!TryParseTrajectoryEvent(rawLine, out TrajectoryEventRecord record))
            {
                AddSelectableEventRow(
                    lines,
                    eventIndex,
                    "invalid event",
                    null,
                    null,
                    null,
                    "Open raw invalid event");
                continue;
            }

            if (string.Equals(record.EventType, "user_input", StringComparison.OrdinalIgnoreCase))
            {
                turnIndex++;
                turnStartedAt = record.TimestampUtc;
                AddTurnSeparator(lines, turnIndex, record.TimestampUtc);
            }

            string location = turnIndex == 0
                ? "Between turns"
                : "Turn " + turnIndex.ToString(CultureInfo.InvariantCulture);
            string timing = FormatTrajectoryTiming(record.TimestampUtc, previousTimestamp, turnStartedAt);
            AddSelectableEventRow(
                lines,
                eventIndex,
                record.EventType,
                record.TimestampUtc,
                record.ToolName,
                location,
                BuildTrajectoryStepSummary(record, timing));
            previousTimestamp = record.TimestampUtc ?? previousTimestamp;
        }

        if (eventIndex == 0)
        {
            AddWrappedStyledText(
                lines,
                "The trajectory event log exists but is empty.",
                contentWidth,
                "yellow");
        }

        return lines;
    }

    private static void OpenReaderSelectionDetails(AppState state)
    {
        if (!string.Equals(state.ReaderViewKind, "trajectory", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(state.ReaderViewDataPath))
        {
            return;
        }

        ReaderViewLine? selected = GetSelectedReaderLine(state);
        if (selected?.SelectionKey is not string selectionKey ||
            !int.TryParse(selectionKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out int eventIndex))
        {
            return;
        }

        int width = Math.Max(40, GetWindowWidth() - 1);
        IReadOnlyList<ReaderViewLine> detailLines = BuildTrajectoryEventDetailLines(
            state.SessionId ?? string.Empty,
            state.ReaderViewDataPath,
            width,
            eventIndex);

        state.ReaderViewParentStyledLines = state.ReaderViewStyledLines;
        state.ReaderViewParentTitle = state.ReaderViewTitle;
        state.ReaderViewParentInstructions = state.ReaderViewInstructions;
        state.ReaderViewParentScrollOffset = state.ReaderScrollOffset;
        state.ReaderViewParentSelectedSelectableIndex = state.ReaderSelectedSelectableIndex;
        state.ReaderViewStyledLines = detailLines;
        state.ReaderViewLines = null;
        state.ReaderViewTitle = "TRAJECTORY STEP";
        state.ReaderViewInstructions = "step details | Up/Down PgUp/PgDn Home/End scroll | Esc/F5 back";
        state.ReaderSelectedSelectableIndex = 0;
        state.ReaderScrollOffset = 0;
        state.ReaderViewDirty = true;
    }

    internal static IReadOnlyList<ReaderViewLine> BuildTrajectoryEventDetailLines(
        string sectionId,
        string eventLogPath,
        int width,
        int selectedEventIndex)
    {
        int contentWidth = Math.Max(32, width);
        List<ReaderViewLine> lines = [];

        AddStyledLine(lines, "Trajectory step details", "[bold aqua]Trajectory step details[/]");
        AddStyledLine(lines, "Section: " + sectionId, "[grey]Section:[/] " + Markup.Escape(sectionId));
        AddStyledLine(lines, "Event log: " + eventLogPath, "[grey]Event log:[/] " + Markup.Escape(eventLogPath));
        AddStyledLine(lines, string.Empty, string.Empty);

        if (!File.Exists(eventLogPath))
        {
            AddWrappedStyledText(lines, "The trajectory event log no longer exists.", contentWidth, "yellow");
            return lines;
        }

        int eventIndex = 0;
        int turnIndex = 0;
        DateTimeOffset? previousTimestamp = null;
        DateTimeOffset? turnStartedAt = null;

        foreach (string rawLine in File.ReadLines(eventLogPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            eventIndex++;
            bool isSelected = eventIndex == selectedEventIndex;
            if (!TryParseTrajectoryEvent(rawLine, out TrajectoryEventRecord record))
            {
                if (isSelected)
                {
                    AddEventHeader(lines, eventIndex, "invalid event", null, null, null, null);
                    AddOptionalTrajectoryBlock(lines, "Raw", rawLine.Trim(), contentWidth, "red");
                    return lines;
                }

                continue;
            }

            if (string.Equals(record.EventType, "user_input", StringComparison.OrdinalIgnoreCase))
            {
                turnIndex++;
                turnStartedAt = record.TimestampUtc;
            }

            string location = turnIndex == 0
                ? "Between turns"
                : "Turn " + turnIndex.ToString(CultureInfo.InvariantCulture);
            string timing = FormatTrajectoryTiming(record.TimestampUtc, previousTimestamp, turnStartedAt);

            if (isSelected)
            {
                AddEventHeader(
                    lines,
                    eventIndex,
                    record.EventType,
                    record.TimestampUtc,
                    record.ToolName,
                    location,
                    timing);
                AddTrajectoryEventDetails(lines, record, contentWidth);
                return lines;
            }

            previousTimestamp = record.TimestampUtc ?? previousTimestamp;
        }

        AddWrappedStyledText(
            lines,
            "That trajectory step could not be found.",
            contentWidth,
            "yellow");
        return lines;
    }

    private static List<TrajectoryEventRecord> LoadTrajectoryEvents(string eventLogPath)
    {
        List<TrajectoryEventRecord> events = [];
        foreach (string rawLine in File.ReadLines(eventLogPath))
        {
            if (TryParseTrajectoryEvent(rawLine, out TrajectoryEventRecord record))
            {
                events.Add(record);
            }
        }

        return events;
    }

    private static TrajectorySummary BuildTrajectorySummary(IReadOnlyList<TrajectoryEventRecord> events)
    {
        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        foreach (TrajectoryEventRecord record in events)
        {
            if (record.TimestampUtc is not DateTimeOffset timestamp)
            {
                continue;
            }

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
            BuildTrajectoryTimeline(events));
    }

    private static int CountEvents(IReadOnlyList<TrajectoryEventRecord> events, string eventType)
    {
        return events.Count(record => string.Equals(record.EventType, eventType, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildTrajectoryTimeline(IReadOnlyList<TrajectoryEventRecord> events)
    {
        if (events.Count == 0)
        {
            return string.Empty;
        }

        const int MaxWidth = 80;
        if (events.Count <= MaxWidth)
        {
            return new string(events.Select(GetTimelineMarker).ToArray());
        }

        char[] markers = new char[MaxWidth];
        for (int index = 0; index < MaxWidth; index++)
        {
            int eventIndex = (int)Math.Floor(index * (events.Count / (double)MaxWidth));
            markers[index] = GetTimelineMarker(events[Math.Min(eventIndex, events.Count - 1)]);
        }

        return new string(markers);
    }

    private static char GetTimelineMarker(TrajectoryEventRecord record)
    {
        string eventType = record.EventType;
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

    private static void AddTrajectorySummary(List<ReaderViewLine> lines, TrajectorySummary summary)
    {
        AddStyledLine(lines, string.Empty, string.Empty);
        AddStyledLine(lines, "Summary", "[bold white]Summary[/]");
        AddStyledLine(
            lines,
            $"  Events: {summary.EventCount}  Turns: {summary.UserTurnCount}  Reasoning: {summary.ReasoningCount}  Assistant output: {summary.AssistantOutputCount}",
            $"[grey]  Events:[/] {summary.EventCount}  [grey]Turns:[/] {summary.UserTurnCount}  [grey]Reasoning:[/] {summary.ReasoningCount}  [grey]Assistant output:[/] {summary.AssistantOutputCount}");
        AddStyledLine(
            lines,
            $"  Tool requests: {summary.ToolRequestCount}  Tool results: {summary.ToolResultCount}  Plans: {summary.PlanCount}  Failures: {summary.FailureCount}",
            $"[grey]  Tool requests:[/] {summary.ToolRequestCount}  [grey]Tool results:[/] {summary.ToolResultCount}  [grey]Plans:[/] {summary.PlanCount}  [grey]Failures:[/] {summary.FailureCount}");

        if (summary.FirstTimestampUtc is not null && summary.LastTimestampUtc is not null)
        {
            TimeSpan duration = summary.LastTimestampUtc.Value - summary.FirstTimestampUtc.Value;
            AddStyledLine(
                lines,
                $"  Time range: {FormatTrajectoryTimestamp(summary.FirstTimestampUtc)} -> {FormatTrajectoryTimestamp(summary.LastTimestampUtc)}  Duration: {FormatDuration(duration)}",
                "[grey]  Time range:[/] " + Markup.Escape(FormatTrajectoryTimestamp(summary.FirstTimestampUtc)) +
                " [grey]->[/] " + Markup.Escape(FormatTrajectoryTimestamp(summary.LastTimestampUtc)) +
                "  [grey]Duration:[/] " + Markup.Escape(FormatDuration(duration)));
        }

        if (summary.ToolNames.Count > 0)
        {
            string tools = string.Join(", ", summary.ToolNames);
            AddStyledLine(lines, "  Tools: " + tools, "[grey]  Tools:[/] " + Markup.Escape(tools));
        }

        if (!string.IsNullOrWhiteSpace(summary.Timeline))
        {
            AddStyledLine(lines, "  Timeline: " + summary.Timeline, "[grey]  Timeline:[/] " + Markup.Escape(summary.Timeline));
            AddStyledLine(
                lines,
                "  Legend: U user M model C chunk A assistant T tool R retry E error P plan",
                "[grey]  Legend:[/] U user  M model  C chunk  A assistant  T tool  R retry  E error  P plan");
        }

        if (summary.WorkingDirectories.Count > 0)
        {
            string directories = string.Join(", ", summary.WorkingDirectories);
            AddStyledLine(lines, "  Working directories: " + directories, "[grey]  Working directories:[/] " + Markup.Escape(directories));
        }
    }

    private static string GetStemCodeApplicationDataDirectoryPath()
    {
        string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            folderPath = string.IsNullOrWhiteSpace(userProfilePath)
                ? Directory.GetCurrentDirectory()
                : Path.Combine(userProfilePath, ".config");
        }

        return Path.Combine(folderPath, "StemCode");
    }

    private static bool TryParseTrajectoryEvent(string json, out TrajectoryEventRecord record)
    {
        record = default;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            record = new TrajectoryEventRecord(
                TryGetDateTimeOffset(root, "timestampUtc"),
                TryGetString(root, "sectionId"),
                TryGetString(root, "parentSessionId"),
                TryGetString(root, "eventType") ?? "event",
                TryGetString(root, "agentProfileName"),
                TryGetString(root, "modelId"),
                TryGetString(root, "workingDirectory"),
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

    private static void AddTurnSeparator(List<ReaderViewLine> lines, int turnIndex, DateTimeOffset? timestampUtc)
    {
        string timestamp = FormatTrajectoryTimestamp(timestampUtc);
        string plain = string.IsNullOrWhiteSpace(timestamp)
            ? $"-- Turn {turnIndex} --"
            : $"-- Turn {turnIndex} - {timestamp} --";
        string markup = "[grey]--[/] [bold magenta]Turn " +
            turnIndex.ToString(CultureInfo.InvariantCulture) +
            "[/] [grey]" +
            Markup.Escape(string.IsNullOrWhiteSpace(timestamp) ? "--" : "- " + timestamp + " --") +
            "[/]";
        AddStyledLine(lines, plain, markup);
    }

    private static void AddEventHeader(
        List<ReaderViewLine> lines,
        int eventIndex,
        string eventType,
        DateTimeOffset? timestampUtc,
        string? toolName,
        string? location,
        string? timing)
    {
        string normalizedEventType = FormatTrajectoryEventType(eventType);
        string timestampText = timestampUtc is null
            ? string.Empty
            : "  " + timestampUtc.Value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
        string toolText = string.IsNullOrWhiteSpace(toolName) ? string.Empty : "  " + toolName.Trim();
        string locationText = string.IsNullOrWhiteSpace(location) ? string.Empty : "  " + location.Trim();
        string timingText = string.IsNullOrWhiteSpace(timing) ? string.Empty : "  " + timing.Trim();
        string plain = $"#{eventIndex:000} {normalizedEventType}{timestampText}{locationText}{timingText}{toolText}";
        string markup =
            $"[bold white]#{eventIndex:000}[/] [bold cyan]{Markup.Escape(normalizedEventType)}[/]" +
            $"[grey]{Markup.Escape(timestampText)}[/]" +
            (string.IsNullOrWhiteSpace(locationText) ? string.Empty : $" [magenta]{Markup.Escape(location!.Trim())}[/]") +
            (string.IsNullOrWhiteSpace(timingText) ? string.Empty : $" [grey]{Markup.Escape(timing!.Trim())}[/]") +
            (string.IsNullOrWhiteSpace(toolText) ? string.Empty : $" [yellow]{Markup.Escape(toolName!.Trim())}[/]");

        AddStyledLine(lines, plain, markup);
    }

    private static void AddSelectableEventRow(
        List<ReaderViewLine> lines,
        int eventIndex,
        string eventType,
        DateTimeOffset? timestampUtc,
        string? toolName,
        string? location,
        string summary)
    {
        string normalizedEventType = FormatTrajectoryEventType(eventType);
        string timestampText = timestampUtc is null
            ? string.Empty
            : timestampUtc.Value.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string toolText = string.IsNullOrWhiteSpace(toolName) ? string.Empty : "  " + toolName.Trim();
        string locationText = string.IsNullOrWhiteSpace(location) ? string.Empty : "  " + location.Trim();
        string suffix = string.IsNullOrWhiteSpace(summary) ? string.Empty : "  " + summary.Trim();
        string plain = $"  #{eventIndex:000} {normalizedEventType,-28} {timestampText}{locationText}{toolText}{suffix}";
        string markup =
            $"  [bold white]#{eventIndex:000}[/] [bold cyan]{Markup.Escape(normalizedEventType)}[/]" +
            (string.IsNullOrWhiteSpace(timestampText) ? string.Empty : $" [grey]{Markup.Escape(timestampText)}[/]") +
            (string.IsNullOrWhiteSpace(locationText) ? string.Empty : $" [magenta]{Markup.Escape(location!.Trim())}[/]") +
            (string.IsNullOrWhiteSpace(toolText) ? string.Empty : $" [yellow]{Markup.Escape(toolName!.Trim())}[/]") +
            (string.IsNullOrWhiteSpace(suffix) ? string.Empty : $" [white]{Markup.Escape(summary.Trim())}[/]");

        lines.Add(new ReaderViewLine(
            markup,
            plain,
            eventIndex.ToString(CultureInfo.InvariantCulture)));
    }

    private static string BuildTrajectoryStepSummary(TrajectoryEventRecord record, string timing)
    {
        List<string> parts = [];
        AddText(parts, "status", record.Status ?? record.ToolStatus);
        AddText(parts, "timing", timing);
        AddText(parts, "summary", TruncateTrajectorySummary(record.Summary ?? record.Text ?? record.ToolMessage));

        if (record.InputTokens is not null || record.OutputTokens is not null || record.ReasoningTokens is not null)
        {
            List<string> usage = [];
            AddNumber(usage, "in", record.InputTokens);
            AddNumber(usage, "out", record.OutputTokens);
            AddNumber(usage, "reason", record.ReasoningTokens);
            parts.Add("tokens=" + string.Join("/", usage));
        }

        return string.Join("  ", parts);
    }

    private static string? TruncateTrajectorySummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string normalized = value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return normalized.Length <= 90
            ? normalized
            : normalized[..87] + "...";
    }

    private static void AddTrajectoryEventDetails(
        List<ReaderViewLine> lines,
        TrajectoryEventRecord record,
        int contentWidth)
    {
        AddTrajectoryMetadata(lines, record, contentWidth);
        AddOptionalTrajectoryBlock(lines, "Summary", record.Summary ?? record.Text, contentWidth, "white");
        AddOptionalTrajectoryBlock(lines, "Input", PrettyJsonOrRaw(record.InputJson), contentWidth, "aqua");
        AddOptionalTrajectoryBlock(lines, "Output", PrettyJsonOrRaw(record.OutputJson) ?? record.Text, contentWidth, "green");
        AddOptionalTrajectoryBlock(lines, "Thinking", PrettyJsonOrRaw(record.ThinkingJson), contentWidth, "yellow");
        AddOptionalTrajectoryBlock(lines, "Source", record.Source, contentWidth, "white");
        AddOptionalTrajectoryBlock(lines, "System Prompt", record.SystemPrompt, contentWidth, "white");
        AddOptionalTrajectoryBlock(lines, "Tools", PrettyJsonOrRaw(record.ToolsJson), contentWidth, "aqua");
        AddOptionalTrajectoryBlock(lines, "Tool Schema", PrettyJsonOrRaw(record.ToolSchemaJson), contentWidth, "aqua");
        AddOptionalTrajectoryBlock(lines, "Request Options", PrettyJsonOrRaw(record.RequestOptionsJson), contentWidth, "aqua");
        AddOptionalTrajectoryBlock(lines, "Usage", FormatTrajectoryUsage(record), contentWidth, "magenta");
        AddOptionalTrajectoryBlock(lines, "Timing", FormatTrajectoryTimingBlock(record), contentWidth, "magenta");
        AddOptionalTrajectoryBlock(lines, "Errors", FormatTrajectoryErrors(record), contentWidth, "red");
        AddOptionalTrajectoryBlock(lines, "Tool arguments", PrettyJsonOrRaw(record.ToolArgumentsJson), contentWidth, "aqua");
        AddOptionalTrajectoryBlock(lines, "Tool message", record.ToolMessage, contentWidth, "white");
        AddOptionalTrajectoryBlock(lines, "Tool result", PrettyJsonOrRaw(record.ToolResultJson), contentWidth, "green");
        AddOptionalTrajectoryBlock(lines, "Raw", PrettyJsonOrRaw(record.RawJson), contentWidth, "grey");
    }

    private static void AddTrajectoryMetadata(List<ReaderViewLine> lines, TrajectoryEventRecord record, int width)
    {
        List<string> metadata = [];
        AddMetadata(metadata, "section", record.SectionId);
        AddMetadata(metadata, "parent", record.ParentSessionId);
        AddMetadata(metadata, "seq", record.EventSequenceId?.ToString(CultureInfo.InvariantCulture));
        AddMetadata(metadata, "turn", record.TurnId);
        AddMetadata(metadata, "step", record.StepId);
        AddMetadata(metadata, "request", record.ModelRequestId);
        AddMetadata(metadata, "toolCall", record.ToolCallId);
        AddMetadata(metadata, "parentCall", record.ParentCallId);
        AddMetadata(metadata, "result", record.ToolResultId);
        AddMetadata(metadata, "model", record.ModelId);
        AddMetadata(metadata, "profile", record.AgentProfileName);
        AddMetadata(metadata, "cwd", record.WorkingDirectory);
        AddMetadata(metadata, "status", record.Status ?? record.ToolStatus);

        if (metadata.Count > 0)
        {
            AddWrappedStyledText(lines, string.Join("  ", metadata), width, "grey", indent: "  ");
        }
    }

    private static void AddMetadata(List<string> values, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(label + "=" + value.Trim());
        }
    }

    private static void AddOptionalTrajectoryBlock(
        List<ReaderViewLine> lines,
        string label,
        string? value,
        int width,
        string style)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        AddStyledLine(lines, "  " + label + ":", "[grey]  " + Markup.Escape(label) + ":[/]");
        AddWrappedStyledText(lines, value.Trim(), width, style, indent: "    ");
    }

    private static void AddWrappedStyledText(
        List<ReaderViewLine> lines,
        string text,
        int width,
        string style,
        string indent = "")
    {
        int wrapWidth = Math.Max(8, width - indent.Length);
        string normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        foreach (string rawLine in normalized.Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                AddStyledLine(lines, indent, Markup.Escape(indent));
                continue;
            }

            foreach (string wrapped in WrapText(rawLine, wrapWidth))
            {
                string plain = indent + wrapped;
                string markup = Markup.Escape(indent) + $"[{style}]{Markup.Escape(wrapped)}[/]";
                AddStyledLine(lines, plain, markup);
            }
        }
    }

    private static void AddStyledLine(List<ReaderViewLine> lines, string plain, string markup)
    {
        lines.Add(new ReaderViewLine(markup, plain));
    }

    private static string FormatTrajectoryEventType(string eventType)
    {
        return string.Join(
            ' ',
            eventType.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string FormatTrajectoryTimestamp(DateTimeOffset? value)
    {
        return value is null
            ? string.Empty
            : value.Value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
    }

    private static string FormatTrajectoryTiming(
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

    private static string FormatDuration(TimeSpan duration)
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

    private static string? PrettyJsonOrRaw(string? value)
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

    private static string? FormatTrajectoryUsage(TrajectoryEventRecord record)
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

    private static string? FormatTrajectoryTimingBlock(TrajectoryEventRecord record)
    {
        List<string> parts = [];
        AddText(parts, "requestStart", FormatTrajectoryTimestamp(record.RequestStartedAtUtc));
        AddText(parts, "firstToken", FormatTrajectoryTimestamp(record.FirstTokenAtUtc));
        AddText(parts, "completed", FormatTrajectoryTimestamp(record.CompletedAtUtc));
        AddDurationMs(parts, "total", record.TotalDurationMs);
        AddDurationMs(parts, "TTFT", record.TtftMs);
        AddDurationMs(parts, "generation", record.GenerationDurationMs);
        if (record.TokensPerSecond is not null)
        {
            parts.Add("tokens/sec=" + record.TokensPerSecond.Value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private static string? FormatTrajectoryErrors(TrajectoryEventRecord record)
    {
        List<string> parts = [];
        AddText(parts, "type", record.ErrorType);
        AddText(parts, "code", record.ErrorCode);
        AddNumber(parts, "retry", record.RetryNumber);
        AddNumber(parts, "maxRetries", record.MaxRetries);
        AddNumber(parts, "delayMs", record.RetryDelayMs);
        return parts.Count == 0 ? null : string.Join("  ", parts);
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

    private readonly record struct TrajectoryEventRecord(
        DateTimeOffset? TimestampUtc,
        string? SectionId,
        string? ParentSessionId,
        string EventType,
        string? AgentProfileName,
        string? ModelId,
        string? WorkingDirectory,
        string? Text,
        string? ToolCallId,
        string? ToolName,
        string? ToolArgumentsJson,
        string? ToolStatus,
        string? ToolMessage,
        string? ToolResultJson,
        string? ErrorType,
        long? EventSequenceId,
        string? TurnId,
        int? TurnIndex,
        string? StepId,
        int? StepIndex,
        string? ModelRequestId,
        string? AssistantMessageId,
        string? ToolResultId,
        string? ParentCallId,
        string? Source,
        string? SystemPrompt,
        string? ToolsJson,
        string? ToolSchemaJson,
        string? RequestOptionsJson,
        string? InputJson,
        string? OutputJson,
        string? ThinkingJson,
        string? RawJson,
        string? Status,
        string? ErrorCode,
        int? RetryNumber,
        int? MaxRetries,
        int? RetryDelayMs,
        int? InputTokens,
        int? CacheReadTokens,
        int? CacheWriteTokens,
        int? OutputTokens,
        int? ReasoningTokens,
        DateTimeOffset? RequestStartedAtUtc,
        DateTimeOffset? FirstTokenAtUtc,
        DateTimeOffset? CompletedAtUtc,
        double? TotalDurationMs,
        double? TtftMs,
        double? GenerationDurationMs,
        double? TokensPerSecond,
        bool? IsPartial,
        bool? IsRunning,
        bool? Interrupted,
        string? Summary,
        int? ItemsReplaced,
        int? TokensReplaced);

    private readonly record struct TrajectorySummary(
        int EventCount,
        int UserTurnCount,
        int ReasoningCount,
        int AssistantOutputCount,
        int ToolRequestCount,
        int ToolResultCount,
        int PlanCount,
        int FailureCount,
        DateTimeOffset? FirstTimestampUtc,
        DateTimeOffset? LastTimestampUtc,
        IReadOnlyList<string> ToolNames,
        IReadOnlyList<string> WorkingDirectories,
        string Timeline);
}
