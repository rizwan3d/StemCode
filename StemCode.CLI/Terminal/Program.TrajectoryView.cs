using StemCode.Application.Models;
using StemCode.Application.Trajectory;
using System.Globalization;
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

        IReadOnlyList<SessionEventRecord> events = LoadTrajectoryEvents(eventLogPath);
        AddTrajectorySummary(lines, TrajectorySummaryBuilder.Build(events));
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

            if (!TryParseTrajectoryEvent(rawLine, out SessionEventRecord record))
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
            previousTimestamp = record.TimestampUtc;
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

        IReadOnlyList<SessionEventRecord> events = LoadTrajectoryEvents(eventLogPath);
        AddTrajectorySummary(lines, TrajectorySummaryBuilder.Build(events));
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
            if (!TryParseTrajectoryEvent(rawLine, out SessionEventRecord record))
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
            previousTimestamp = record.TimestampUtc;
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
            if (!TryParseTrajectoryEvent(rawLine, out SessionEventRecord record))
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

            previousTimestamp = record.TimestampUtc;
        }

        AddWrappedStyledText(
            lines,
            "That trajectory step could not be found.",
            contentWidth,
            "yellow");
        return lines;
    }

    private static IReadOnlyList<SessionEventRecord> LoadTrajectoryEvents(string eventLogPath)
    {
        return TrajectoryLogReader.LoadFromFile(eventLogPath);
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

    private static bool TryParseTrajectoryEvent(string json, out SessionEventRecord record)
    {
        return TrajectoryLogReader.TryParseLine(json, out record);
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

    private static string BuildTrajectoryStepSummary(SessionEventRecord record, string timing)
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
        SessionEventRecord record,
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

    private static void AddTrajectoryMetadata(List<ReaderViewLine> lines, SessionEventRecord record, int width)
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
        return TrajectoryFormatting.FormatEventType(eventType);
    }

    private static string FormatTrajectoryTimestamp(DateTimeOffset? value)
    {
        return TrajectoryFormatting.FormatTimestamp(value);
    }

    private static string FormatTrajectoryTiming(
        DateTimeOffset? timestampUtc,
        DateTimeOffset? previousTimestampUtc,
        DateTimeOffset? turnStartedAtUtc)
    {
        return TrajectoryFormatting.FormatTiming(timestampUtc, previousTimestampUtc, turnStartedAtUtc);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return TrajectoryFormatting.FormatDuration(duration);
    }

    private static string? PrettyJsonOrRaw(string? value)
    {
        return TrajectoryFormatting.PrettyJsonOrRaw(value);
    }

    private static string? FormatTrajectoryUsage(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatUsage(record);
    }

    private static string? FormatTrajectoryTimingBlock(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatTimingBlock(record);
    }

    private static string? FormatTrajectoryErrors(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatErrors(record);
    }

    private static void AddNumber(List<string> parts, string label, int? value)
    {
        if (value is not null)
        {
            parts.Add(label + "=" + value.Value.ToString(CultureInfo.InvariantCulture));
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
