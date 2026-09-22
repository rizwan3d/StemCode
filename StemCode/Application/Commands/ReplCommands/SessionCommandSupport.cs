using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Storage;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StemCode.Application.Commands;

internal static class SessionCommandSupport
{
    public const int DefaultCompactRetainedTurns = 4;

    public static ConversationSectionSnapshot CreateSnapshot(
        ReplSessionContext session,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.CreateSectionSnapshot(updatedAtUtc);
    }

    public static ConversationSectionSnapshot CreateCopySnapshot(
        ReplSessionContext source,
        string title,
        IReadOnlyList<ConversationSectionTurn> turns,
        int totalEstimatedOutputTokens,
        bool includeState)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(turns);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ConversationSectionSnapshot(
            Guid.NewGuid().ToString("D"),
            title,
            now,
            now,
            source.ProviderProfile,
            source.ActiveModelId,
            source.AvailableModelIds,
            turns,
            Math.Max(0, totalEstimatedOutputTokens),
            includeState ? source.PendingExecutionPlan : null,
            source.AgentProfile.Name,
            source.ReasoningEffort,
            source.ThinkingMode,
            includeState ? source.SessionState : SessionStateSnapshot.Empty,
            source.WorkspacePath,
            source.ModelContextWindowTokens,
            source.ModelContextMetadata,
            source.ActiveProviderName);
    }

    public static async Task<ReplSessionContext> SaveAndResumeAsync(
        ConversationSectionSnapshot snapshot,
        IConversationSectionStore sectionStore,
        ISessionAppService sessionAppService,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(sectionStore);
        ArgumentNullException.ThrowIfNull(sessionAppService);

        await sectionStore.SaveAsync(snapshot, cancellationToken);
        return await sessionAppService.ResumeAsync(
            new ResumeSessionRequest(snapshot.SectionId),
            cancellationToken);
    }

    public static string CreateDefaultExportPath(
        ReplSessionContext session,
        string extension)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        string normalizedExtension = extension.Trim().TrimStart('.');
        string title = SanitizeFileName(session.SectionTitle);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "session";
        }

        string fileName = $"stemcode-{title}-{session.SectionId[..8]}.{normalizedExtension}";
        return Path.Combine(Directory.GetCurrentDirectory(), fileName);
    }

    public static string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string expanded = path.Trim().Trim('"');
        if (expanded.StartsWith("~/", StringComparison.Ordinal) ||
            expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = Path.Combine(home, expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }

    public static async Task ExportJsonAsync(
        ConversationSectionSnapshot snapshot,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureParentDirectory(filePath);
        await using FileStream stream = new(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        await JsonSerializer.SerializeAsync(
            stream,
            snapshot,
            ConversationSectionStorageJsonContext.Default.ConversationSectionSnapshot,
            cancellationToken);
    }

    public static async Task<ConversationSectionSnapshot?> LoadJsonAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        try
        {
            return await JsonSerializer.DeserializeAsync(
                stream,
                ConversationSectionStorageJsonContext.Default.ConversationSectionSnapshot,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static async Task ExportHtmlAsync(
        ConversationSectionSnapshot snapshot,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureParentDirectory(filePath);
        string html = CreateHtmlTranscript(snapshot);
        await File.WriteAllTextAsync(filePath, html, Encoding.UTF8, cancellationToken);
    }

    public static async Task ExportTrajectoryHtmlAsync(
        ConversationSectionSnapshot snapshot,
        string eventLogPath,
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventLogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        EnsureParentDirectory(filePath);
        IReadOnlyList<SessionEventRecord> events = await LoadEventLogAsync(
            eventLogPath,
            cancellationToken);
        string html = CreateTrajectoryHtml(snapshot, events, eventLogPath);
        await File.WriteAllTextAsync(filePath, html, Encoding.UTF8, cancellationToken);
    }

    public static ConversationSectionSnapshot CreateImportedSnapshot(
        ConversationSectionSnapshot imported,
        ReplSessionContext currentSession)
    {
        ArgumentNullException.ThrowIfNull(imported);
        ArgumentNullException.ThrowIfNull(currentSession);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string title = imported.Title.EndsWith(" imported", StringComparison.OrdinalIgnoreCase)
            ? imported.Title
            : imported.Title + " imported";

        return new ConversationSectionSnapshot(
            Guid.NewGuid().ToString("D"),
            title,
            now,
            now,
            imported.ProviderProfile,
            imported.ActiveModelId,
            imported.AvailableModelIds,
            imported.Turns,
            imported.TotalEstimatedOutputTokens,
            imported.PendingExecutionPlan,
            imported.AgentProfileName,
            imported.ReasoningEffort,
            imported.ThinkingMode,
            imported.SessionState,
            currentSession.WorkspacePath,
            imported.ModelContextWindowTokens,
            imported.ModelContextMetadata,
            imported.ActiveProviderName);
    }

    public static bool TryNormalizeSessionId(string value, out string sessionId)
    {
        sessionId = string.Empty;
        if (!Guid.TryParse(value.Trim(), out Guid parsed))
        {
            return false;
        }

        sessionId = parsed.ToString("D");
        return true;
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);
    }

    public static string CreateTitleWithSuffix(string title, string suffix)
    {
        string normalizedTitle = string.IsNullOrWhiteSpace(title)
            ? ReplSessionContext.DefaultSectionTitle
            : title.Trim();

        return normalizedTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalizedTitle
            : normalizedTitle + " " + suffix;
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

    private static string CreateHtmlTranscript(ConversationSectionSnapshot snapshot)
    {
        StringBuilder builder = new();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>");
        builder.Append(Html(snapshot.Title));
        builder.AppendLine("</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(":root{color-scheme:light dark;font-family:Inter,Segoe UI,Arial,sans-serif;background:#f7f7f5;color:#20201d}");
        builder.AppendLine("body{margin:0;padding:40px;line-height:1.55}");
        builder.AppendLine("main{max-width:960px;margin:0 auto}");
        builder.AppendLine("header{border-bottom:1px solid #d9d7d0;margin-bottom:28px;padding-bottom:20px}");
        builder.AppendLine("h1{font-size:28px;margin:0 0 10px}");
        builder.AppendLine(".meta{display:grid;grid-template-columns:160px 1fr;gap:6px 16px;color:#54524b;font-size:14px}");
        builder.AppendLine(".turn{border-top:1px solid #ddd9d0;padding:24px 0}");
        builder.AppendLine(".role{font-size:12px;font-weight:700;text-transform:uppercase;letter-spacing:0;color:#6a675e;margin:0 0 8px}");
        builder.AppendLine("pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#fff;border:1px solid #dedbd3;border-radius:8px;padding:14px;margin:0}");
        builder.AppendLine(".tool{margin:10px 0;color:#54524b;font-size:14px}");
        builder.AppendLine("@media (prefers-color-scheme:dark){:root{background:#171717;color:#eeece6}.meta,.role,.tool{color:#b8b3a7}header,.turn{border-color:#3a3935}pre{background:#20201e;border-color:#3c3a35}}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine("<header>");
        builder.Append("<h1>");
        builder.Append(Html(snapshot.Title));
        builder.AppendLine("</h1>");
        builder.AppendLine("<div class=\"meta\">");
        AppendMeta(builder, "Session", snapshot.SectionId);
        AppendMeta(builder, "Created", FormatTimestamp(snapshot.CreatedAtUtc));
        AppendMeta(builder, "Updated", FormatTimestamp(snapshot.UpdatedAtUtc));
        AppendMeta(builder, "Provider", snapshot.ProviderProfile.ProviderKind.ToDisplayName());
        AppendMeta(builder, "Model", snapshot.ActiveModelId);
        AppendMeta(builder, "Profile", snapshot.AgentProfileName);
        AppendMeta(builder, "Turns", snapshot.Turns.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("</div>");
        builder.AppendLine("</header>");

        for (int index = 0; index < snapshot.Turns.Count; index++)
        {
            ConversationSectionTurn turn = snapshot.Turns[index];
            builder.AppendLine("<section class=\"turn\">");
            builder.Append("<p class=\"role\">User turn ");
            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("</p>");
            AppendPre(builder, turn.UserInput ?? string.Empty);

            if (turn.ToolOutputMessages.Count > 0 || turn.ToolCalls.Count > 0)
            {
                builder.AppendLine("<div class=\"tool\">");
                foreach (string output in turn.ToolOutputMessages)
                {
                    builder.Append("<p>");
                    builder.Append(Html(CreatePreview(output, 180)));
                    builder.AppendLine("</p>");
                }

                foreach (ConversationToolCall call in turn.ToolCalls)
                {
                    builder.Append("<p>Tool: ");
                    builder.Append(Html(call.Name));
                    builder.AppendLine("</p>");
                }

                builder.AppendLine("</div>");
            }

            builder.AppendLine("<p class=\"role\">Assistant</p>");
            AppendPre(builder, turn.AssistantResponse ?? string.Empty);
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static async Task<IReadOnlyList<SessionEventRecord>> LoadEventLogAsync(
        string eventLogPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(eventLogPath))
        {
            return [];
        }

        List<SessionEventRecord> events = [];
        await foreach (string line in File.ReadLinesAsync(eventLogPath, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                SessionEventRecord? record = JsonSerializer.Deserialize(
                    line,
                    SessionEventLogJsonContext.Default.SessionEventRecord);
                if (record is not null)
                {
                    events.Add(record);
                }
            }
            catch (JsonException)
            {
            }
        }

        return events
            .OrderBy(static record => record.EventSequenceId ?? long.MaxValue)
            .ThenBy(static record => record.TimestampUtc)
            .ToArray();
    }

    private static string CreateTrajectoryHtml(
        ConversationSectionSnapshot snapshot,
        IReadOnlyList<SessionEventRecord> events,
        string eventLogPath)
    {
        StringBuilder builder = new();
        builder.AppendLine("<!doctype html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("<meta charset=\"utf-8\">");
        builder.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<title>");
        builder.Append(Html(snapshot.Title));
        builder.AppendLine(" trajectory</title>");
        builder.AppendLine("<style>");
        builder.AppendLine(":root{color-scheme:light dark;font-family:Inter,Segoe UI,Arial,sans-serif;background:#f7f7f5;color:#20201d}");
        builder.AppendLine("body{margin:0;padding:32px;line-height:1.5}");
        builder.AppendLine("main{max-width:1120px;margin:0 auto}");
        builder.AppendLine("header{border-bottom:1px solid #d9d7d0;margin-bottom:24px;padding-bottom:18px}");
        builder.AppendLine("h1{font-size:28px;margin:0 0 10px}");
        builder.AppendLine("h2{font-size:18px;margin:28px 0 12px}");
        builder.AppendLine(".meta{display:grid;grid-template-columns:180px 1fr;gap:6px 16px;color:#54524b;font-size:14px}");
        builder.AppendLine(".event{border-top:1px solid #ddd9d0;padding:18px 0}");
        builder.AppendLine(".event-head{display:flex;gap:10px;align-items:baseline;flex-wrap:wrap;margin-bottom:8px}");
        builder.AppendLine(".badge{font-size:12px;font-weight:700;text-transform:uppercase;background:#e9e6dc;border:1px solid #d8d4c8;border-radius:999px;padding:2px 8px}");
        builder.AppendLine(".timeline{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere;background:#fff;border:1px solid #dedbd3;border-radius:8px;padding:10px;margin:16px 0}");
        builder.AppendLine(".time{color:#6a675e;font-size:13px}");
        builder.AppendLine(".label{color:#6a675e;font-size:13px;margin:10px 0 4px}");
        builder.AppendLine(".tabs{display:flex;gap:6px;flex-wrap:wrap;margin:8px 0}.tab{font-size:12px;color:#54524b;background:#f0eee8;border:1px solid #ddd9d0;border-radius:999px;padding:2px 8px}");
        builder.AppendLine("pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#fff;border:1px solid #dedbd3;border-radius:8px;padding:12px;margin:0}");
        builder.AppendLine(".empty{color:#6a675e;border-top:1px solid #ddd9d0;padding-top:18px}");
        builder.AppendLine("@media (prefers-color-scheme:dark){:root{background:#171717;color:#eeece6}.meta,.time,.label,.empty,.tab{color:#b8b3a7}header,.event,.empty{border-color:#3a3935}.badge,.tab{background:#26251f;border-color:#454238}pre,.timeline{background:#20201e;border-color:#3c3a35}}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine("<header>");
        builder.Append("<h1>");
        builder.Append(Html(snapshot.Title));
        builder.AppendLine(" trajectory</h1>");
        builder.AppendLine("<div class=\"meta\">");
        AppendMeta(builder, "Session", snapshot.SectionId);
        AppendMeta(builder, "Created", FormatTimestamp(snapshot.CreatedAtUtc));
        AppendMeta(builder, "Updated", FormatTimestamp(snapshot.UpdatedAtUtc));
        AppendMeta(builder, "Provider", snapshot.ProviderProfile.ProviderKind.ToDisplayName());
        AppendMeta(builder, "Model", snapshot.ActiveModelId);
        AppendMeta(builder, "Profile", snapshot.AgentProfileName);
        AppendMeta(builder, "Turns", snapshot.Turns.Count.ToString(CultureInfo.InvariantCulture));
        AppendMeta(builder, "Events", events.Count.ToString(CultureInfo.InvariantCulture));
        AppendMeta(builder, "Event log", eventLogPath);
        builder.AppendLine("</div>");
        builder.AppendLine("</header>");
        if (events.Count > 0)
        {
            builder.AppendLine("<h2>Timeline</h2>");
            builder.Append("<div class=\"timeline\">");
            builder.Append(Html(BuildTrajectoryTimeline(events)));
            builder.AppendLine("</div>");
            builder.AppendLine("<p class=\"time\">Legend: U user, M model/prompt, C chunk, A assistant, T tool, R retry, E error, P plan.</p>");
        }

        builder.AppendLine("<h2>Event Stream</h2>");

        if (events.Count == 0)
        {
            builder.AppendLine("<p class=\"empty\">No trajectory events were recorded for this session.</p>");
        }

        foreach (SessionEventRecord record in events)
        {
            builder.AppendLine("<section class=\"event\">");
            builder.AppendLine("<div class=\"event-head\">");
            builder.Append("<span class=\"badge\">");
            builder.Append(Html(FormatEventType(record.EventType)));
            builder.AppendLine("</span>");
            builder.Append("<span class=\"time\">");
            builder.Append(Html(FormatTimestamp(record.TimestampUtc)));
            builder.AppendLine("</span>");
            if (!string.IsNullOrWhiteSpace(record.ToolName))
            {
                builder.Append("<span class=\"time\">");
                builder.Append(Html(record.ToolName));
                builder.AppendLine("</span>");
            }

            builder.AppendLine("</div>");
            builder.AppendLine("<div class=\"tabs\"><span class=\"tab\">Summary</span><span class=\"tab\">Input</span><span class=\"tab\">Output</span><span class=\"tab\">Thinking</span><span class=\"tab\">Source</span><span class=\"tab\">System Prompt</span><span class=\"tab\">Tools</span><span class=\"tab\">Tool Schema</span><span class=\"tab\">Request Options</span><span class=\"tab\">Usage</span><span class=\"tab\">Timing</span><span class=\"tab\">Errors</span></div>");
            AppendOptionalBlock(builder, "Summary", record.Summary ?? record.Text);
            AppendOptionalBlock(builder, "Input", PrettyJsonOrRaw(record.InputJson));
            AppendOptionalBlock(builder, "Output", PrettyJsonOrRaw(record.OutputJson) ?? record.Text);
            AppendOptionalBlock(builder, "Thinking", PrettyJsonOrRaw(record.ThinkingJson));
            AppendOptionalBlock(builder, "Source", record.Source);
            AppendOptionalBlock(builder, "System Prompt", record.SystemPrompt);
            AppendOptionalBlock(builder, "Tools", PrettyJsonOrRaw(record.ToolsJson));
            AppendOptionalBlock(builder, "Tool Schema", PrettyJsonOrRaw(record.ToolSchemaJson));
            AppendOptionalBlock(builder, "Request Options", PrettyJsonOrRaw(record.RequestOptionsJson));
            AppendOptionalBlock(builder, "Usage", FormatUsage(record));
            AppendOptionalBlock(builder, "Timing", FormatTiming(record));
            AppendOptionalBlock(builder, "Errors", FormatErrors(record));
            AppendOptionalBlock(builder, "Tool arguments", PrettyJsonOrRaw(record.ToolArgumentsJson));
            AppendOptionalBlock(builder, "Tool message", record.ToolMessage);
            AppendOptionalBlock(builder, "Tool result", PrettyJsonOrRaw(record.ToolResultJson));
            AppendOptionalBlock(builder, "Status", record.ToolStatus);
            AppendOptionalBlock(builder, "Error type", record.ErrorType);
            AppendOptionalBlock(builder, "Raw", PrettyJsonOrRaw(record.RawJson));
            builder.AppendLine("</section>");
        }

        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void AppendMeta(StringBuilder builder, string label, string value)
    {
        builder.Append("<div>");
        builder.Append(Html(label));
        builder.AppendLine("</div>");
        builder.Append("<div>");
        builder.Append(Html(value));
        builder.AppendLine("</div>");
    }

    private static void AppendPre(StringBuilder builder, string value)
    {
        builder.Append("<pre>");
        builder.Append(Html(value));
        builder.AppendLine("</pre>");
    }

    private static void AppendOptionalBlock(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("<p class=\"label\">");
        builder.Append(Html(label));
        builder.AppendLine("</p>");
        AppendPre(builder, value);
    }

    private static string FormatEventType(string eventType)
    {
        return string.Join(
            ' ',
            eventType.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
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
            using (Utf8JsonWriter writer = new(
                stream,
                new JsonWriterOptions { Indented = true }))
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

    private static string BuildTrajectoryTimeline(IReadOnlyList<SessionEventRecord> events)
    {
        if (events.Count == 0)
        {
            return string.Empty;
        }

        const int MaxWidth = 120;
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

    private static char GetTimelineMarker(SessionEventRecord record)
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

    private static string? FormatUsage(SessionEventRecord record)
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

    private static string? FormatTiming(SessionEventRecord record)
    {
        List<string> parts = [];
        AddText(parts, "requestStart", FormatOptionalTimestamp(record.RequestStartedAtUtc));
        AddText(parts, "firstToken", FormatOptionalTimestamp(record.FirstTokenAtUtc));
        AddText(parts, "completed", FormatOptionalTimestamp(record.CompletedAtUtc));
        AddDuration(parts, "total", record.TotalDurationMs);
        AddDuration(parts, "TTFT", record.TtftMs);
        AddDuration(parts, "generation", record.GenerationDurationMs);
        if (record.TokensPerSecond is not null)
        {
            parts.Add("tokens/sec=" + record.TokensPerSecond.Value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? null : string.Join("  ", parts);
    }

    private static string? FormatErrors(SessionEventRecord record)
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

    private static void AddDuration(List<string> parts, string label, double? milliseconds)
    {
        if (milliseconds is not null)
        {
            parts.Add(label + "=" + FormatDuration(TimeSpan.FromMilliseconds(milliseconds.Value)));
        }
    }

    private static void AddText(List<string> parts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(label + "=" + value.Trim());
        }
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

    private static string? FormatOptionalTimestamp(DateTimeOffset? value)
    {
        return value is null ? null : FormatTimestamp(value.Value);
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static void EnsureParentDirectory(string filePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        HashSet<char> invalid = new(Path.GetInvalidFileNameChars());
        StringBuilder builder = new();
        foreach (char character in value.Trim().ToLowerInvariant())
        {
            if (invalid.Contains(character))
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : '-');
        }

        string sanitized = builder.ToString().Trim('-');
        while (sanitized.Contains("--", StringComparison.Ordinal))
        {
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);
        }

        return sanitized.Length <= 48
            ? sanitized
            : sanitized[..48].Trim('-');
    }
}
