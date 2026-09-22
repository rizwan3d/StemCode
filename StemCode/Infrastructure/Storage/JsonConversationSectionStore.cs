using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using System.Text.Json;

namespace StemCode.Infrastructure.Storage;

internal sealed class JsonConversationSectionStore : IConversationSectionStore
{
    private readonly IUserDataPathProvider _pathProvider;

    public JsonConversationSectionStore(IUserDataPathProvider pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public async Task<ConversationSectionSnapshot?> LoadAsync(
        string sectionId,
        CancellationToken cancellationToken)
    {
        string filePath = GetSectionFilePath(sectionId);
        if (!File.Exists(filePath))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(filePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (!IsSectionSnapshotJson(json))
            {
                return null;
            }

            ConversationSectionSnapshot? snapshot = JsonSerializer.Deserialize(
                json,
                ConversationSectionStorageJsonContext.Default.ConversationSectionSnapshot);
            return snapshot is null
                ? null
                : NormalizeLegacySnapshot(snapshot);
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

    private static bool IsSectionSnapshotJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sectionId", out JsonElement sectionId) ||
            sectionId.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(sectionId.GetString());
    }

    public async Task<IReadOnlyList<ConversationSectionSnapshot>> ListAsync(
        CancellationToken cancellationToken)
    {
        string directoryPath = _pathProvider.GetSessionsDirectoryPath();
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        List<ConversationSectionSnapshot> snapshots = [];

        foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string sectionId = Path.GetFileNameWithoutExtension(filePath);
            ConversationSectionSnapshot? snapshot = await LoadAsync(sectionId, cancellationToken);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots
            .OrderByDescending(static snapshot => snapshot.UpdatedAtUtc)
            .ToArray();
    }

    public async Task SaveAsync(
        ConversationSectionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string filePath = GetSectionFilePath(snapshot.SectionId);
        string directoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Section path does not contain a parent directory.");

        FilePermissionHelper.EnsurePrivateDirectory(directoryPath);

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

        await stream.FlushAsync(cancellationToken);
        FilePermissionHelper.EnsurePrivateFile(filePath);
    }

    private string GetSectionFilePath(string sectionId)
    {
        string normalizedSectionId = NormalizeSectionId(sectionId);
        return Path.Combine(
            _pathProvider.GetSessionsDirectoryPath(),
            $"{normalizedSectionId}.json");
    }

    private static string NormalizeSectionId(string sectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);

        if (!Guid.TryParse(sectionId.Trim(), out Guid parsedSectionId))
        {
            throw new ArgumentException(
                "Section id must be a valid GUID.",
                nameof(sectionId));
        }

        return parsedSectionId.ToString("D");
    }

    private static ConversationSectionSnapshot NormalizeLegacySnapshot(ConversationSectionSnapshot snapshot)
    {
        ConversationSectionTurn[] normalizedTurns = snapshot.Turns
            .Select(static turn =>
                turn.Status == ConversationTurnStatus.Pending &&
                !string.IsNullOrWhiteSpace(turn.AssistantResponse) &&
                turn.FailureInfo is null
                    ? new ConversationSectionTurn(
                        userInput: turn.UserInput,
                        assistantResponse: turn.AssistantResponse,
                        toolCalls: turn.ToolCalls,
                        toolOutputMessages: turn.ToolOutputMessages,
                        assistantReasoningContent: turn.AssistantReasoningContent,
                        assistantReasoningDetailsJson: turn.AssistantReasoningDetailsJson,
                        status: ConversationTurnStatus.Completed,
                        turnId: turn.TurnId,
                        attachments: turn.Attachments)
                    : turn)
            .ToArray();

        return new ConversationSectionSnapshot(
            snapshot.SectionId,
            snapshot.Title,
            snapshot.CreatedAtUtc,
            snapshot.UpdatedAtUtc,
            snapshot.ProviderProfile,
            snapshot.ActiveModelId,
            snapshot.AvailableModelIds,
            normalizedTurns,
            snapshot.TotalEstimatedOutputTokens,
            snapshot.PendingExecutionPlan,
            snapshot.AgentProfileName,
            snapshot.ReasoningEffort,
            snapshot.ThinkingMode,
            snapshot.SessionState,
            snapshot.WorkspacePath,
            snapshot.ModelContextWindowTokens,
            snapshot.ModelContextMetadata,
            snapshot.ActiveProviderName,
            snapshot.ParentSessionId);
    }
}
