using StemCode.Application.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StemCode.Infrastructure.Storage;

/// <summary>
/// Reads and writes <c>Application:Terminal</c> in agent-profile.json. Reads merge
/// the user file with the workspace's <c>.stemcode/agent-profile.json</c>; writes go
/// to the user file because these are personal preferences.
/// </summary>
internal sealed class TerminalSettingsStore : ITerminalSettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly IUserDataPathProvider _userDataPathProvider;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;

    public TerminalSettingsStore(
        IUserDataPathProvider userDataPathProvider,
        IWorkspaceRootProvider workspaceRootProvider)
    {
        _userDataPathProvider = userDataPathProvider;
        _workspaceRootProvider = workspaceRootProvider;
    }

    public TerminalSettings Load()
    {
        TerminalSettings settings = new();
        string workspaceFile = Path.Combine(
            Path.GetFullPath(_workspaceRootProvider.GetWorkspaceRoot()),
            ".stemcode",
            "agent-profile.json");
        foreach (string path in new[] { _userDataPathProvider.GetConfigurationFilePath(), workspaceFile })
        {
            if (TryGetTerminalNode(path) is JsonObject terminal)
            {
                Merge(settings, terminal);
            }
        }

        return settings;
    }

    public Task SaveOutputStyleAsync(string? outputStyle, CancellationToken cancellationToken)
    {
        return UpdateUserTerminalAsync(
            terminal => terminal["OutputStyle"] = string.IsNullOrWhiteSpace(outputStyle) ? null : outputStyle.Trim(),
            cancellationToken);
    }

    public Task SaveVimModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        return UpdateUserTerminalAsync(terminal => terminal["VimMode"] = enabled, cancellationToken);
    }

    private async Task UpdateUserTerminalAsync(Action<JsonObject> update, CancellationToken cancellationToken)
    {
        string path = _userDataPathProvider.GetConfigurationFilePath();
        JsonObject root = File.Exists(path) && JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) is JsonObject existing
            ? existing
            : [];
        JsonObject application = GetOrCreate(root, "Application");
        JsonObject terminal = GetOrCreate(application, "Terminal");
        update(terminal);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, root.ToJsonString(WriteOptions) + Environment.NewLine, cancellationToken);
    }

    private static JsonObject GetOrCreate(JsonObject parent, string name)
    {
        JsonNode? node = FindProperty(parent, name);
        if (node is JsonObject existing)
        {
            return existing;
        }

        JsonObject created = [];
        parent[name] = created;
        return created;
    }

    private static JsonNode? TryGetTerminalNode(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return null;
            }

            return FindProperty(root, "Application") is JsonObject application
                ? FindProperty(application, "Terminal")
                : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Merge(TerminalSettings settings, JsonObject terminal)
    {
        if (FindProperty(terminal, "OutputStyle") is JsonValue style && style.TryGetValue(out string? styleName))
        {
            settings.OutputStyle = string.IsNullOrWhiteSpace(styleName) ? null : styleName.Trim();
        }

        if (FindProperty(terminal, "VimMode") is JsonValue vim && vim.TryGetValue(out bool vimMode))
        {
            settings.VimMode = vimMode;
        }

        if (FindProperty(terminal, "StatusLine") is JsonObject statusLine)
        {
            if (FindProperty(statusLine, "Command") is JsonValue command && command.TryGetValue(out string? commandText))
            {
                settings.StatusLineCommand = string.IsNullOrWhiteSpace(commandText) ? null : commandText.Trim();
            }

            if (FindProperty(statusLine, "RefreshSeconds") is JsonValue refresh && refresh.TryGetValue(out int seconds))
            {
                settings.StatusLineRefreshSeconds = Math.Clamp(seconds, 1, 3600);
            }
        }
    }

    // Configuration keys are case-insensitive elsewhere in StemCode, so match them that way here too.
    private static JsonNode? FindProperty(JsonObject parent, string name)
    {
        foreach (KeyValuePair<string, JsonNode?> property in parent)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
