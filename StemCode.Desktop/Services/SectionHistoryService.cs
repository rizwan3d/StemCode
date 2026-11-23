using StemCode.Desktop.Models;
using System.Text.Json;

namespace StemCode.Desktop.Services;

public sealed class SectionHistoryService
{
    private const string SectionsDirectoryName = "sessions";
    private const string StorageDirectoryName = "StemCode";

    public async Task<IReadOnlyList<WorkspaceSectionInfo>> ListSectionsAsync(
        string workspacePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return [];
        }

        string normalizedWorkspacePath = NormalizePath(workspacePath);
        string sectionsDirectory = GetSessionsDirectoryPath();
        if (!Directory.Exists(sectionsDirectory))
        {
            return [];
        }

        List<WorkspaceSectionInfo> sections = [];

        foreach (string filePath in Directory.EnumerateFiles(sectionsDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            WorkspaceSectionInfo? section = await TryReadSectionAsync(
                filePath,
                normalizedWorkspacePath,
                cancellationToken);

            if (section is not null)
            {
                sections.Add(section);
            }
        }

        return sections
            .OrderByDescending(static section => section.UpdatedAtUtc)
            .ToArray();
    }

    public async Task<bool> DeleteSectionAsync(
        string workspacePath,
        string sectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            string.IsNullOrWhiteSpace(sectionId) ||
            !Guid.TryParse(sectionId.Trim(), out Guid parsedSectionId))
        {
            return false;
        }

        string normalizedWorkspacePath;
        try
        {
            normalizedWorkspacePath = NormalizePath(workspacePath);
        }
        catch
        {
            return false;
        }

        string filePath = Path.Combine(
            GetSessionsDirectoryPath(),
            $"{parsedSectionId:D}.json");

        if (!File.Exists(filePath))
        {
            return false;
        }

        WorkspaceSectionInfo? section = await TryReadSectionAsync(
            filePath,
            normalizedWorkspacePath,
            cancellationToken);

        if (section is null ||
            !string.Equals(section.SectionId, parsedSectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            File.Delete(filePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<WorkspaceSectionInfo?> TryReadSectionAsync(
        string filePath,
        string normalizedWorkspacePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(filePath);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);

            JsonElement root = document.RootElement;
            string? sectionWorkspacePath = TryGetString(root, "workspacePath");

            if (!IsSamePath(sectionWorkspacePath, normalizedWorkspacePath))
            {
                return null;
            }

            string sectionId = TryGetString(root, "sectionId") ??
                Path.GetFileNameWithoutExtension(filePath);
            string title = TryGetString(root, "title") ?? "Untitled section";
            string activeModelId = TryGetString(root, "activeModelId") ?? "model";
            DateTimeOffset updatedAtUtc = TryGetDateTimeOffset(root, "updatedAtUtc") ??
                File.GetLastWriteTimeUtc(filePath);
            int turnCount = TryGetArrayLength(root, "turns");

            return new WorkspaceSectionInfo(
                sectionId,
                title,
                updatedAtUtc,
                turnCount,
                activeModelId,
                NormalizePath(sectionWorkspacePath!));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string GetSessionsDirectoryPath()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(root, StorageDirectoryName, SectionsDirectoryName);
    }

    private static bool IsSamePath(string? candidatePath, string normalizedWorkspacePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        string normalizedCandidatePath;
        try
        {
            normalizedCandidatePath = NormalizePath(candidatePath);
        }
        catch
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(normalizedCandidatePath, normalizedWorkspacePath, comparison);
    }

    private static string NormalizePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;

        return fullPath.Length <= root.Length
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.TryGetDateTimeOffset(out DateTimeOffset value)
            ? value
            : null;
    }

    private static int TryGetArrayLength(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;
    }
}
