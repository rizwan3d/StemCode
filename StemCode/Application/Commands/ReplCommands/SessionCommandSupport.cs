using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Trajectory;
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
        return TrajectoryFormatting.CreatePreview(value, maxLength);
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
        return await TrajectoryLogReader.LoadFromFileAsync(eventLogPath, cancellationToken);
    }

    private static string CreateTrajectoryHtml(
        ConversationSectionSnapshot snapshot,
        IReadOnlyList<SessionEventRecord> events,
        string eventLogPath)
    {
        DateTimeOffset? firstTimestamp = events.Count == 0
            ? null
            : events.Min(static record => record.TimestampUtc);
        DateTimeOffset? lastTimestamp = events.Count == 0
            ? null
            : events.Max(static record => record.TimestampUtc);
        string[] toolNames = events
            .Select(static record => record.ToolName)
            .Where(static toolName => !string.IsNullOrWhiteSpace(toolName))
            .Select(static toolName => toolName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static toolName => toolName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int userTurnCount = CountEvents(events, "user_input");
        int modelEventCount = events.Count(static record =>
            record.EventType.Contains("model", StringComparison.OrdinalIgnoreCase) ||
            record.EventType.Contains("prompt", StringComparison.OrdinalIgnoreCase));
        int toolEventCount = events.Count(static record =>
            record.EventType.Contains("tool", StringComparison.OrdinalIgnoreCase));
        int failureCount = events.Count(static record =>
            record.EventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            record.EventType.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(record.ErrorType));

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
        builder.AppendLine(":root{color-scheme:light dark;font-family:Inter,Segoe UI,Arial,sans-serif;background:#f4f6f8;color:#18202a;--panel:#fff;--panel2:#f9fafb;--line:#d7dee8;--muted:#627083;--strong:#0f1720;--chip:#eef3f8;--code:#fbfcfe;--blue:#2563eb;--green:#047857;--yellow:#a16207;--red:#b42318;--violet:#6d28d9}");
        builder.AppendLine("*{box-sizing:border-box}body{margin:0;line-height:1.5}main{max-width:1440px;margin:0 auto;padding:28px}header{display:grid;grid-template-columns:minmax(0,1fr) 360px;gap:24px;align-items:start;margin-bottom:24px}.eyebrow{color:var(--blue);font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;margin:0 0 6px}h1{font-size:30px;line-height:1.12;margin:0 0 12px}h2{font-size:16px;margin:0 0 12px}.subtitle{color:var(--muted);margin:0;max-width:760px}.panel{background:var(--panel);border:1px solid var(--line);border-radius:8px;box-shadow:0 1px 2px rgba(15,23,32,.04)}.meta{display:grid;grid-template-columns:112px minmax(0,1fr);gap:7px 14px;color:var(--muted);font-size:13px;padding:14px}.meta div:nth-child(2n){color:var(--strong);overflow-wrap:anywhere}.metrics{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:10px;margin:18px 0}.metric{padding:12px 14px}.metric strong{display:block;color:var(--strong);font-size:22px;line-height:1}.metric span{display:block;color:var(--muted);font-size:12px;margin-top:4px}.layout{display:grid;grid-template-columns:360px minmax(0,1fr);gap:18px;align-items:start}.sidebar{position:sticky;top:18px;padding:14px;max-height:calc(100vh - 36px);overflow:auto}.timeline{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;overflow-wrap:anywhere;background:var(--code);border:1px solid var(--line);border-radius:6px;padding:10px;margin:0 0 10px;color:var(--strong);font-size:12px;line-height:1.65}.legend{color:var(--muted);font-size:12px;margin:0 0 16px}.outline{display:grid;gap:7px}.outline a{display:grid;grid-template-columns:46px minmax(0,1fr);gap:9px;text-decoration:none;color:inherit;border:1px solid transparent;border-radius:6px;padding:7px}.outline a:hover{background:var(--panel2);border-color:var(--line)}.outline .num{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--muted);font-size:12px}.outline .name{font-weight:700;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.outline .hint{grid-column:2;color:var(--muted);font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.stream{display:grid;gap:10px}.event{scroll-margin-top:16px;overflow:hidden}.event summary{list-style:none;cursor:pointer;padding:14px 16px;display:grid;grid-template-columns:64px minmax(150px,1fr) auto;gap:12px;align-items:center;background:var(--panel)}.event summary::-webkit-details-marker{display:none}.event[open] summary{border-bottom:1px solid var(--line);background:var(--panel2)}.index{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--muted);font-size:13px}.event-title{min-width:0}.event-name{font-weight:800;color:var(--strong);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.event-preview{color:var(--muted);font-size:13px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-top:2px}.chips{display:flex;flex-wrap:wrap;justify-content:flex-end;gap:6px}.chip{font-size:12px;color:var(--muted);background:var(--chip);border:1px solid var(--line);border-radius:999px;padding:2px 8px;white-space:nowrap}.chip.tool{color:var(--violet)}.chip.ok{color:var(--green)}.chip.warn{color:var(--yellow)}.chip.err{color:var(--red)}.event-body{padding:14px 16px 16px}.facts{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-bottom:12px}.fact{background:var(--panel2);border:1px solid var(--line);border-radius:6px;padding:8px 10px;min-width:0}.fact span{display:block;color:var(--muted);font-size:11px;text-transform:uppercase;font-weight:800}.fact strong{display:block;color:var(--strong);font-size:13px;overflow-wrap:anywhere}.block{border-top:1px solid var(--line);padding-top:12px;margin-top:12px}.label{color:var(--muted);font-size:12px;font-weight:800;text-transform:uppercase;margin:0 0 6px}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:var(--code);border:1px solid var(--line);border-radius:6px;padding:12px;margin:0;font-size:13px;line-height:1.45}.empty{color:var(--muted);padding:20px}.tools{display:flex;flex-wrap:wrap;gap:6px;margin-top:10px}.tool-pill{font-size:12px;color:var(--violet);background:var(--chip);border:1px solid var(--line);border-radius:999px;padding:3px 8px}.turn{margin:16px 0 8px;color:var(--muted);font-size:12px;font-weight:800;text-transform:uppercase;letter-spacing:.08em}.turn:before,.turn:after{content:\"\";display:inline-block;width:30px;border-top:1px solid var(--line);vertical-align:middle;margin:0 8px 3px 0}.turn:after{margin:0 0 3px 8px}.sr{position:absolute;left:-10000px}");
        builder.AppendLine("@media (max-width:980px){main{padding:18px}header,.layout{grid-template-columns:1fr}.sidebar{position:static;max-height:none}.metrics{grid-template-columns:repeat(2,minmax(0,1fr))}.event summary{grid-template-columns:52px minmax(0,1fr)}.chips{grid-column:1 / -1;justify-content:flex-start}.facts{grid-template-columns:1fr}}@media (prefers-color-scheme:dark){:root{background:#111418;color:#e5eaf0;--panel:#171b21;--panel2:#1d222a;--line:#303844;--muted:#9aa6b5;--strong:#f4f7fb;--chip:#222936;--code:#101318;--blue:#7aa2ff;--green:#4ade80;--yellow:#facc15;--red:#f87171;--violet:#c4b5fd}.panel{box-shadow:none}}");
        builder.AppendLine("</style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("<main>");
        builder.AppendLine("<header>");
        builder.AppendLine("<div>");
        builder.AppendLine("<p class=\"eyebrow\">Trajectory harness</p>");
        builder.Append("<h1>");
        builder.Append(Html(snapshot.Title));
        builder.AppendLine(" trajectory</h1>");
        builder.AppendLine("<p class=\"subtitle\">A readable event-by-event view of the run, with the outline on the left and compact payload details in each step.</p>");
        builder.AppendLine("</div>");
        builder.AppendLine("<div class=\"panel meta\">");
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

        builder.AppendLine("<section class=\"metrics\" aria-label=\"Run metrics\">");
        AppendMetric(builder, events.Count, "events");
        AppendMetric(builder, userTurnCount, "user turns");
        AppendMetric(builder, modelEventCount, "model events");
        AppendMetric(builder, toolEventCount, "tool events");
        AppendMetric(builder, failureCount, "errors");
        AppendMetric(builder, firstTimestamp is null || lastTimestamp is null
            ? string.Empty
            : TrajectoryFormatting.FormatDuration(lastTimestamp.Value - firstTimestamp.Value), "span");
        builder.AppendLine("</section>");

        builder.AppendLine("<div class=\"layout\">");
        builder.AppendLine("<aside class=\"panel sidebar\">");
        if (events.Count > 0)
        {
            builder.AppendLine("<h2>Run outline</h2>");
            builder.Append("<div class=\"timeline\">");
            builder.Append(Html(TrajectorySummaryBuilder.BuildTimeline(events, maxWidth: 120)));
            builder.AppendLine("</div>");
            builder.AppendLine("<p class=\"legend\">Legend: U user, M model/prompt, C chunk, A assistant, T tool, R retry, E error, P plan.</p>");
            if (toolNames.Length > 0)
            {
                builder.AppendLine("<div class=\"tools\">");
                foreach (string toolName in toolNames)
                {
                    builder.Append("<span class=\"tool-pill\">");
                    builder.Append(Html(toolName));
                    builder.AppendLine("</span>");
                }

                builder.AppendLine("</div>");
            }

            builder.AppendLine("<nav class=\"outline\" aria-label=\"Trajectory steps\">");
            for (int index = 0; index < events.Count; index++)
            {
                SessionEventRecord record = events[index];
                builder.Append("<a href=\"#event-");
                builder.Append((index + 1).ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("\">");
                builder.Append("<span class=\"num\">#");
                builder.Append((index + 1).ToString("000", CultureInfo.InvariantCulture));
                builder.AppendLine("</span>");
                builder.Append("<span class=\"name\">");
                builder.Append(Html(FormatEventType(record.EventType)));
                builder.AppendLine("</span>");
                builder.Append("<span class=\"hint\">");
                builder.Append(Html(BuildEventPreview(record)));
                builder.AppendLine("</span>");
                builder.AppendLine("</a>");
            }

            builder.AppendLine("</nav>");
        }
        builder.AppendLine("</aside>");

        builder.AppendLine("<section class=\"stream\" aria-label=\"Event stream\">");

        if (events.Count == 0)
        {
            builder.AppendLine("<div class=\"panel empty\">No trajectory events were recorded for this session.</div>");
        }

        int? lastTurnIndex = null;
        for (int index = 0; index < events.Count; index++)
        {
            SessionEventRecord record = events[index];
            if (record.TurnIndex is not null && record.TurnIndex != lastTurnIndex)
            {
                lastTurnIndex = record.TurnIndex;
                builder.Append("<div class=\"turn\">Turn ");
                builder.Append((record.TurnIndex.Value + 1).ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("</div>");
            }

            builder.Append("<details class=\"panel event\" id=\"event-");
            builder.Append((index + 1).ToString(CultureInfo.InvariantCulture));
            builder.Append('"');
            if (index < 3)
            {
                builder.Append(" open");
            }

            builder.AppendLine(">");
            builder.AppendLine("<summary>");
            builder.Append("<span class=\"index\">#");
            builder.Append((index + 1).ToString("000", CultureInfo.InvariantCulture));
            builder.AppendLine("</span>");
            builder.AppendLine("<span class=\"event-title\">");
            builder.Append("<span class=\"event-name\">");
            builder.Append(Html(FormatEventType(record.EventType)));
            builder.AppendLine("</span>");
            builder.Append("<span class=\"event-preview\">");
            builder.Append(Html(BuildEventPreview(record)));
            builder.AppendLine("</span>");
            builder.AppendLine("</span>");
            builder.AppendLine("<span class=\"chips\">");
            AppendChip(builder, FormatTimestamp(record.TimestampUtc), null);
            AppendChip(builder, BuildTurnStepText(record), null);
            if (!string.IsNullOrWhiteSpace(record.ToolName))
            {
                AppendChip(builder, record.ToolName, "tool");
            }

            AppendChip(builder, record.Status ?? record.ToolStatus, GetStatusChipClass(record));
            builder.AppendLine("</span>");
            builder.AppendLine("</summary>");
            builder.AppendLine("<div class=\"event-body\">");
            builder.AppendLine("<div class=\"facts\">");
            AppendFact(builder, "Sequence", record.EventSequenceId?.ToString(CultureInfo.InvariantCulture));
            AppendFact(builder, "Turn", BuildTurnStepText(record));
            AppendFact(builder, "Model", record.ModelId);
            AppendFact(builder, "Request", record.ModelRequestId);
            AppendFact(builder, "Tool call", record.ToolCallId);
            AppendFact(builder, "Working dir", record.WorkingDirectory);
            builder.AppendLine("</div>");
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
            builder.AppendLine("</div>");
            builder.AppendLine("</details>");
        }

        builder.AppendLine("</section>");
        builder.AppendLine("</div>");
        builder.AppendLine("</main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static int CountEvents(IReadOnlyList<SessionEventRecord> events, string eventType)
    {
        return events.Count(record => string.Equals(record.EventType, eventType, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendMetric(StringBuilder builder, int value, string label)
    {
        AppendMetric(builder, value.ToString(CultureInfo.InvariantCulture), label);
    }

    private static void AppendMetric(StringBuilder builder, string value, string label)
    {
        builder.AppendLine("<div class=\"panel metric\">");
        builder.Append("<strong>");
        builder.Append(Html(string.IsNullOrWhiteSpace(value) ? "-" : value));
        builder.AppendLine("</strong>");
        builder.Append("<span>");
        builder.Append(Html(label));
        builder.AppendLine("</span>");
        builder.AppendLine("</div>");
    }

    private static void AppendChip(StringBuilder builder, string? value, string? className)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append("<span class=\"chip");
        if (!string.IsNullOrWhiteSpace(className))
        {
            builder.Append(' ');
            builder.Append(Html(className));
        }

        builder.Append("\">");
        builder.Append(Html(value.Trim()));
        builder.AppendLine("</span>");
    }

    private static void AppendFact(StringBuilder builder, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine("<div class=\"fact\">");
        builder.Append("<span>");
        builder.Append(Html(label));
        builder.AppendLine("</span>");
        builder.Append("<strong>");
        builder.Append(Html(value.Trim()));
        builder.AppendLine("</strong>");
        builder.AppendLine("</div>");
    }

    private static string BuildTurnStepText(SessionEventRecord record)
    {
        List<string> parts = [];
        if (record.TurnIndex is not null)
        {
            parts.Add("turn " + (record.TurnIndex.Value + 1).ToString(CultureInfo.InvariantCulture));
        }

        if (record.StepIndex is not null)
        {
            parts.Add("step " + (record.StepIndex.Value + 1).ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" / ", parts);
    }

    private static string BuildEventPreview(SessionEventRecord record)
    {
        string? value = record.Summary ??
            record.Text ??
            record.ToolMessage ??
            FormatUsage(record) ??
            FormatTiming(record) ??
            record.Source ??
            record.ToolName;

        return string.IsNullOrWhiteSpace(value)
            ? "No summary payload"
            : CreatePreview(value, 128);
    }

    private static string? GetStatusChipClass(SessionEventRecord record)
    {
        string? status = record.Status ?? record.ToolStatus;
        if (!string.IsNullOrWhiteSpace(record.ErrorType) ||
            !string.IsNullOrWhiteSpace(record.ErrorCode) ||
            record.EventType.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            record.EventType.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return "err";
        }

        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        if (status.Contains("success", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("ok", StringComparison.OrdinalIgnoreCase))
        {
            return "ok";
        }

        if (status.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("partial", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("running", StringComparison.OrdinalIgnoreCase))
        {
            return "warn";
        }

        return null;
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
        return TrajectoryFormatting.FormatEventType(eventType);
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

    private static string? FormatUsage(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatUsage(record);
    }

    private static string? FormatTiming(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatTimingBlock(record);
    }

    private static string? FormatErrors(SessionEventRecord record)
    {
        return TrajectoryFormatting.FormatErrors(record);
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
