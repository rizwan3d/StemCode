using System.Text;

namespace StemCode.Application.Commands;

public sealed record CustomSlashCommandDescriptor(
    string Command,
    string Usage,
    string Description,
    bool RequiresArgument);

public sealed record CustomSlashCommandResolution(
    string Command,
    string SourcePath,
    string ExpandedPrompt);

public static class CustomSlashCommandService
{
    private const string ArgumentsPlaceholder = "$ARGUMENTS";
    private const string CommandsDirectoryName = "commands";
    private const string WorkspaceDirectoryName = ".stemcode";

    private static readonly HashSet<string> ReservedCommandNames = new(
        [
            "allow",
            "budget",
            "clear",
            "clone",
            "compact",
            "config",
            "copy",
            "deny",
            "exit",
            "export",
            "fork",
            "help",
            "import",
            "init",
            "ls",
            "mcp",
            "models",
            "new",
            "onboard",
            "permissions",
            "plugin",
            "skill",
            "lesson",
            "lessons",
            "provider",
            "profile",
            "redact",
            "read",
            "redo",
            "rewind",
            "reload",
            "resume",
            "rules",
            "session",
            "setting",
            "share",
            "thinking",
            "tree",
            "undo",
            "update",
            "use",
            "worktree"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CustomSlashCommandDescriptor> List(
        string workspaceRoot,
        string? userCommandsDirectory = null)
    {
        return LoadDefinitions(workspaceRoot, userCommandsDirectory)
            .Values
            .OrderBy(static definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static definition => new CustomSlashCommandDescriptor(
                "/" + definition.Name,
                CreateUsage(definition),
                definition.Description,
                RequiresArgument(definition)))
            .ToArray();
    }

    public static bool TryExpand(
        string workspaceRoot,
        string input,
        out CustomSlashCommandResolution? resolution,
        out string? error,
        string? userCommandsDirectory = null)
    {
        resolution = null;
        error = null;

        if (!TryParseInvocation(input, out CommandInvocation invocation) ||
            IsReservedCommandName(invocation.Name))
        {
            return false;
        }

        IReadOnlyDictionary<string, CustomSlashCommandDefinition> definitions =
            LoadDefinitions(workspaceRoot, userCommandsDirectory);
        if (!definitions.TryGetValue(invocation.Name, out CustomSlashCommandDefinition? definition))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.Body))
        {
            error = $"Custom command '/{definition.Name}' has no prompt body.";
            return true;
        }

        resolution = new CustomSlashCommandResolution(
            "/" + definition.Name,
            definition.SourcePath,
            ExpandBody(definition, invocation));
        return true;
    }

    private static IReadOnlyDictionary<string, CustomSlashCommandDefinition> LoadDefinitions(
        string workspaceRoot,
        string? userCommandsDirectory)
    {
        Dictionary<string, CustomSlashCommandDefinition> definitions = new(StringComparer.OrdinalIgnoreCase);

        foreach (string root in GetCommandRoots(workspaceRoot, userCommandsDirectory))
        {
            foreach (CustomSlashCommandDefinition definition in LoadDefinitionsFromRoot(root))
            {
                if (IsReservedCommandName(definition.Name))
                {
                    continue;
                }

                definitions[definition.Name] = definition;
            }
        }

        return definitions;
    }

    private static IEnumerable<string> GetCommandRoots(
        string workspaceRoot,
        string? userCommandsDirectory)
    {
        string? userRoot = string.IsNullOrWhiteSpace(userCommandsDirectory)
            ? GetDefaultUserCommandsDirectory()
            : userCommandsDirectory;

        if (!string.IsNullOrWhiteSpace(userRoot))
        {
            yield return userRoot;
        }

        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            yield return Path.Combine(
                Path.GetFullPath(workspaceRoot),
                WorkspaceDirectoryName,
                CommandsDirectoryName);
        }
    }

    private static string? GetDefaultUserCommandsDirectory()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, WorkspaceDirectoryName, CommandsDirectoryName);
    }

    private static IEnumerable<CustomSlashCommandDefinition> LoadDefinitionsFromRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            !Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string path in files)
        {
            CustomSlashCommandDefinition? definition = TryLoadDefinition(root, path);
            if (definition is not null)
            {
                yield return definition;
            }
        }
    }

    private static CustomSlashCommandDefinition? TryLoadDefinition(
        string root,
        string path)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        MarkdownCommandDocument document = ParseDocument(content);
        string derivedName = DeriveCommandName(root, path);
        string? configuredName = document.Metadata.TryGetValue("name", out string? nameValue)
            ? NormalizeCommandName(nameValue)
            : null;
        string namespacePrefix = DeriveNamespace(root, path);
        string commandName = configuredName is null
            ? derivedName
            : configuredName.Contains(':', StringComparison.Ordinal) ||
              string.IsNullOrWhiteSpace(namespacePrefix)
                ? configuredName
                : namespacePrefix + ":" + configuredName;

        if (string.IsNullOrWhiteSpace(commandName) ||
            IsReservedCommandName(commandName))
        {
            return null;
        }

        string description = document.Metadata.TryGetValue("description", out string? descriptionValue)
            ? ParseScalar(descriptionValue)
            : "Run a custom prompt.";
        IReadOnlyList<string> args = document.Metadata.TryGetValue("args", out string? argsValue)
            ? ParseArgs(argsValue)
            : [];

        return new CustomSlashCommandDefinition(
            commandName,
            string.IsNullOrWhiteSpace(description)
                ? "Run a custom prompt."
                : description,
            args,
            document.Body.Trim(),
            path);
    }

    private static MarkdownCommandDocument ParseDocument(string content)
    {
        string normalizedContent = content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        using StringReader reader = new(normalizedContent);
        string? firstLine = reader.ReadLine();
        if (!string.Equals(firstLine?.Trim(), "---", StringComparison.Ordinal))
        {
            return new MarkdownCommandDocument(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                normalizedContent.Trim());
        }

        List<string> metadataLines = [];
        bool foundClosingFence = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
            {
                foundClosingFence = true;
                break;
            }

            metadataLines.Add(line);
        }

        if (!foundClosingFence)
        {
            return new MarkdownCommandDocument(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                normalizedContent.Trim());
        }

        return new MarkdownCommandDocument(
            ParseMetadata(metadataLines),
            reader.ReadToEnd().Trim());
    }

    private static Dictionary<string, string> ParseMetadata(IEnumerable<string> lines)
    {
        Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 ||
                trimmed.StartsWith('#'))
            {
                continue;
            }

            int separatorIndex = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = trimmed[..separatorIndex].Trim();
            string value = trimmed[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                metadata[key] = value;
            }
        }

        return metadata;
    }

    private static string ExpandBody(
        CustomSlashCommandDefinition definition,
        CommandInvocation invocation)
    {
        string expanded = definition.Body;
        bool usedArgumentPlaceholder = expanded.Contains(ArgumentsPlaceholder, StringComparison.Ordinal) ||
            expanded.Contains("${ARGUMENTS}", StringComparison.Ordinal);

        expanded = expanded
            .Replace(ArgumentsPlaceholder, invocation.ArgumentText, StringComparison.Ordinal)
            .Replace("${ARGUMENTS}", invocation.ArgumentText, StringComparison.Ordinal);

        for (int index = 0; index < definition.Args.Count; index++)
        {
            string argName = definition.Args[index];
            string argValue = index < invocation.Arguments.Count
                ? invocation.Arguments[index]
                : string.Empty;

            bool usedNamedPlaceholder = expanded.Contains("$" + argName, StringComparison.Ordinal) ||
                expanded.Contains("${" + argName + "}", StringComparison.Ordinal);
            usedArgumentPlaceholder |= usedNamedPlaceholder;
            expanded = expanded
                .Replace("$" + argName, argValue, StringComparison.Ordinal)
                .Replace("${" + argName + "}", argValue, StringComparison.Ordinal);
        }

        if (!usedArgumentPlaceholder &&
            !string.IsNullOrWhiteSpace(invocation.ArgumentText))
        {
            expanded += "\n\nArguments: " + invocation.ArgumentText;
        }

        return expanded.Trim();
    }

    private static bool TryParseInvocation(
        string input,
        out CommandInvocation invocation)
    {
        invocation = default;

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        string trimmed = input.Trim();
        if (!trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string body = trimmed[1..].TrimStart();
        if (body.Length == 0)
        {
            return false;
        }

        int splitIndex = body.IndexOfAny([' ', '\t', '\r', '\n']);
        string rawName = splitIndex < 0
            ? body
            : body[..splitIndex];
        string? name = NormalizeCommandName(rawName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string argumentText = splitIndex < 0
            ? string.Empty
            : body[(splitIndex + 1)..].Trim();
        invocation = new CommandInvocation(
            name,
            argumentText,
            SplitArguments(argumentText));
        return true;
    }

    private static string DeriveCommandName(
        string root,
        string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        string withoutExtension = Path.Combine(
            Path.GetDirectoryName(relativePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(relativePath));
        return NormalizeCommandName(withoutExtension) ?? string.Empty;
    }

    private static string DeriveNamespace(
        string root,
        string path)
    {
        string relativePath = Path.GetRelativePath(root, path);
        string? directory = Path.GetDirectoryName(relativePath);
        return string.IsNullOrWhiteSpace(directory)
            ? string.Empty
            : NormalizeCommandName(directory) ?? string.Empty;
    }

    private static string? NormalizeCommandName(string value)
    {
        string normalized = ParseScalar(value)
            .Trim()
            .TrimStart('/')
            .Replace('\\', ':')
            .Replace('/', ':');
        string[] parts = normalized
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0 ||
            parts.Any(static part => part.Length == 0 || part.Any(static ch => !IsCommandNameCharacter(ch))))
        {
            return null;
        }

        return string.Join(':', parts);
    }

    private static bool IsCommandNameCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
            value is '-' or '_';
    }

    private static bool IsReservedCommandName(string commandName)
    {
        return ReservedCommandNames.Contains(commandName);
    }

    private static string CreateUsage(CustomSlashCommandDefinition definition)
    {
        if (definition.Args.Count > 0)
        {
            return "/" + definition.Name + " " + string.Join(
                " ",
                definition.Args.Select(static arg => "<" + arg + ">"));
        }

        return RequiresArgument(definition)
            ? "/" + definition.Name + " <arguments>"
            : "/" + definition.Name + " [arguments]";
    }

    private static bool RequiresArgument(CustomSlashCommandDefinition definition)
    {
        return definition.Args.Count > 0 ||
            definition.Body.Contains(ArgumentsPlaceholder, StringComparison.Ordinal) ||
            definition.Body.Contains("${ARGUMENTS}", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> ParseArgs(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('[') &&
            trimmed.EndsWith(']'))
        {
            trimmed = trimmed[1..^1];
        }

        return SplitCsv(trimmed)
            .Select(ParseScalar)
            .Select(static value => value.Trim().TrimStart('$'))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Where(static value => value.All(IsCommandNameCharacter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> SplitArguments(string argumentText)
    {
        if (string.IsNullOrWhiteSpace(argumentText))
        {
            return [];
        }

        return argumentText.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyList<string> SplitCsv(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        List<string> values = [];
        StringBuilder current = new();
        char quote = '\0';
        foreach (char ch in value)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                    continue;
                }

                current.Append(ch);
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (ch == ',')
            {
                AddCurrent();
                continue;
            }

            current.Append(ch);
        }

        AddCurrent();
        return values;

        void AddCurrent()
        {
            string item = current.ToString().Trim();
            if (item.Length > 0)
            {
                values.Add(item);
            }

            current.Clear();
        }
    }

    private static string ParseScalar(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    private sealed record MarkdownCommandDocument(
        IReadOnlyDictionary<string, string> Metadata,
        string Body);

    private sealed record CustomSlashCommandDefinition(
        string Name,
        string Description,
        IReadOnlyList<string> Args,
        string Body,
        string SourcePath);

    private readonly record struct CommandInvocation(
        string Name,
        string ArgumentText,
        IReadOnlyList<string> Arguments);
}
