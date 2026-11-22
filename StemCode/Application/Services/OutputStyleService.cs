using StemCode.Application.Abstractions;

namespace StemCode.Application.Services;

public sealed record OutputStyle(
    string Name,
    string Description,
    string? Prompt,
    string Source);

/// <summary>
/// Output styles change how the main agent writes its answers by adding
/// instructions to its system prompt. Built-in styles ship with StemCode; custom
/// ones are Markdown files in <c>.stemcode/output-styles/</c> (workspace) or
/// <c>output-styles/</c> next to the user's agent-profile.json, with optional
/// <c>name</c> and <c>description</c> front matter.
/// </summary>
public interface IOutputStyleService
{
    IReadOnlyList<OutputStyle> List();

    OutputStyle Active { get; }

    Task<OutputStyle> SetActiveAsync(string name, CancellationToken cancellationToken);
}

internal sealed class OutputStyleService : IOutputStyleService
{
    public const string DefaultStyleName = "default";

    private static readonly OutputStyle[] BuiltInStyles =
    [
        new(DefaultStyleName, "StemCode's normal style: focused on getting the work done.", null, "built-in"),
        new(
            "explanatory",
            "Explains the reasoning behind choices and points out patterns in the codebase as it works.",
            """
            Output style: Explanatory.
            While completing the task, help the user understand the codebase and your decisions. Before or after
            meaningful steps, add a short "Insight" note (2-3 bullet points) about why you chose an approach, a
            convention or pattern this codebase uses, or a trade-off worth knowing. Keep insights specific to the
            code at hand rather than generic programming advice, and keep the task itself moving.
            """,
            "built-in"),
        new(
            "learning",
            "Teaches by doing: leaves small, well-scoped pieces for you to write, marked TODO(human).",
            """
            Output style: Learning.
            The user wants to learn by writing code themselves. Do the scaffolding and the parts that are
            mechanical, but for one or two small, meaningful pieces of logic (a function body, a condition, a
            test case) leave a clearly marked `TODO(human)` placeholder and explain what to write and why,
            with hints rather than the answer. Stop and ask the user to fill it in before continuing work that
            depends on it. Add brief "Insight" notes explaining important concepts as you go.
            """,
            "built-in"),
        new(
            "concise",
            "Short, direct answers with minimal explanation.",
            """
            Output style: Concise.
            Keep every message as short as it can be while still correct: no preamble, no recap of what you just
            did unless asked, no restating the question. Prefer a one-line answer, a short list, or just the
            change. Explain only when the user asks or when something is surprising or risky.
            """,
            "built-in")
    ];

    private readonly ITerminalSettingsStore _settingsStore;
    private readonly IUserDataPathProvider _userDataPathProvider;
    private readonly IWorkspaceRootProvider _workspaceRootProvider;
    private string? _activeName;

    public OutputStyleService(
        ITerminalSettingsStore settingsStore,
        IUserDataPathProvider userDataPathProvider,
        IWorkspaceRootProvider workspaceRootProvider)
    {
        _settingsStore = settingsStore;
        _userDataPathProvider = userDataPathProvider;
        _workspaceRootProvider = workspaceRootProvider;
    }

    public OutputStyle Active
    {
        get
        {
            string name = _activeName ??= _settingsStore.Load().OutputStyle ?? DefaultStyleName;
            return Find(name) ?? BuiltInStyles[0];
        }
    }

    public IReadOnlyList<OutputStyle> List()
    {
        Dictionary<string, OutputStyle> styles = new(StringComparer.OrdinalIgnoreCase);
        foreach (OutputStyle style in BuiltInStyles)
        {
            styles[style.Name] = style;
        }

        // Workspace styles are loaded last so they can override user and built-in ones.
        foreach ((string directory, string source) in new[]
                 {
                     (Path.Combine(Path.GetDirectoryName(_userDataPathProvider.GetConfigurationFilePath()) ?? string.Empty, "output-styles"), "user"),
                     (Path.Combine(_workspaceRootProvider.GetWorkspaceRoot(), ".stemcode", "output-styles"), "workspace")
                 })
        {
            foreach (OutputStyle style in LoadDirectory(directory, source))
            {
                styles[style.Name] = style;
            }
        }

        return styles.Values.ToArray();
    }

    public async Task<OutputStyle> SetActiveAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        OutputStyle style = Find(name)
            ?? throw new ArgumentException(
                $"Unknown output style '{name}'. Available: {string.Join(", ", List().Select(static candidate => candidate.Name))}.");
        await _settingsStore.SaveOutputStyleAsync(
            string.Equals(style.Name, DefaultStyleName, StringComparison.OrdinalIgnoreCase) ? null : style.Name,
            cancellationToken);
        _activeName = style.Name;
        return style;
    }

    private OutputStyle? Find(string name)
    {
        return List().FirstOrDefault(style => string.Equals(style.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<OutputStyle> LoadDirectory(string directory, string source)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.md");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files.Order(StringComparer.OrdinalIgnoreCase))
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            (Dictionary<string, string> frontMatter, string body) = ParseFrontMatter(content);
            string name = frontMatter.TryGetValue("name", out string? declaredName) && !string.IsNullOrWhiteSpace(declaredName)
                ? declaredName.Trim()
                : Path.GetFileNameWithoutExtension(file);
            string description = frontMatter.TryGetValue("description", out string? declaredDescription) && !string.IsNullOrWhiteSpace(declaredDescription)
                ? declaredDescription.Trim()
                : $"Custom {source} output style.";
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            yield return new OutputStyle(name, description, $"Output style: {name}.{Environment.NewLine}{body.Trim()}", source);
        }
    }

    private static (Dictionary<string, string> FrontMatter, string Body) ParseFrontMatter(string content)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return (values, normalized);
        }

        int end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            return (values, normalized);
        }

        foreach (string line in normalized[4..end].Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                values[line[..colon].Trim()] = line[(colon + 1)..].Trim().Trim('"', '\'');
            }
        }

        int bodyStart = normalized.IndexOf('\n', end + 4);
        return (values, bodyStart < 0 ? string.Empty : normalized[(bodyStart + 1)..]);
    }
}
