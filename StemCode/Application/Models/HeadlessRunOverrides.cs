namespace StemCode.Application.Models;

/// <summary>
/// Run-scoped overrides from headless/CI command-line flags (<c>--model</c>,
/// <c>--allowedTools</c>, <c>--disallowedTools</c>, <c>--append-system-prompt</c>).
/// They apply to every session the process runs, including ones started with
/// <c>/new</c>, and are never written to configuration.
/// </summary>
public sealed class HeadlessRunOverrides
{
    public static HeadlessRunOverrides None { get; } = new(null, null, [], []);

    public HeadlessRunOverrides(
        string? modelId,
        string? appendSystemPrompt,
        IReadOnlyList<ToolRuleSpec> allowedTools,
        IReadOnlyList<ToolRuleSpec> disallowedTools)
    {
        ArgumentNullException.ThrowIfNull(allowedTools);
        ArgumentNullException.ThrowIfNull(disallowedTools);

        ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
        AppendSystemPrompt = string.IsNullOrWhiteSpace(appendSystemPrompt) ? null : appendSystemPrompt.Trim();
        AllowedTools = allowedTools.ToArray();
        DisallowedTools = disallowedTools.ToArray();

        // A disallowed tool with no target pattern is removed from the model's tool
        // list entirely; one with a pattern stays visible but its matching calls are denied.
        HiddenToolNames = new HashSet<string>(
            DisallowedTools
                .Where(static spec => spec.Pattern is null)
                .Select(static spec => spec.Tool),
            StringComparer.OrdinalIgnoreCase);
    }

    public string? ModelId { get; }

    public string? AppendSystemPrompt { get; }

    public IReadOnlyList<ToolRuleSpec> AllowedTools { get; }

    public IReadOnlyList<ToolRuleSpec> DisallowedTools { get; }

    public IReadOnlySet<string> HiddenToolNames { get; }

    public bool IsEmpty =>
        ModelId is null &&
        AppendSystemPrompt is null &&
        AllowedTools.Count == 0 &&
        DisallowedTools.Count == 0;

    /// <summary>
    /// Permission rules for the allow/deny lists. Deny rules come last so they win
    /// over an allow rule for the same tool.
    /// </summary>
    public IReadOnlyList<PermissionRule> CreatePermissionRules()
    {
        return AllowedTools
            .Select(static spec => spec.ToRule(PermissionMode.Allow))
            .Concat(DisallowedTools.Select(static spec => spec.ToRule(PermissionMode.Deny)))
            .ToArray();
    }
}

/// <summary>
/// One entry of a <c>--allowedTools</c>/<c>--disallowedTools</c> list: a tool name or
/// permission tag, optionally followed by a target pattern in parentheses, for
/// example <c>shell_command(git *)</c> or <c>file_write(src/**)</c>.
/// </summary>
public sealed record ToolRuleSpec(string Tool, string? Pattern)
{
    public PermissionRule ToRule(PermissionMode mode)
    {
        return new PermissionRule
        {
            Mode = mode,
            Tools = [Tool],
            Patterns = Pattern is null ? [] : [Pattern]
        };
    }

    public override string ToString()
    {
        return Pattern is null ? Tool : $"{Tool}({Pattern})";
    }

    /// <summary>
    /// Parses a list separated by commas and/or whitespace. Separators inside
    /// parentheses belong to the pattern, so <c>"shell_command(git log), file_read"</c>
    /// yields two entries.
    /// </summary>
    public static IReadOnlyList<ToolRuleSpec> ParseList(string? value)
    {
        List<ToolRuleSpec> specs = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return specs;
        }

        System.Text.StringBuilder current = new();
        int depth = 0;
        foreach (char character in value)
        {
            if (character == '(')
            {
                depth++;
            }
            else if (character == ')' && depth > 0)
            {
                depth--;
            }

            if (depth == 0 && (character == ',' || char.IsWhiteSpace(character)))
            {
                AddSpec(specs, current.ToString());
                current.Clear();
                continue;
            }

            current.Append(character);
        }

        if (depth != 0)
        {
            throw new ArgumentException($"Unbalanced parentheses in tool list '{value}'.");
        }

        AddSpec(specs, current.ToString());
        return specs;
    }

    private static void AddSpec(
        List<ToolRuleSpec> specs,
        string token)
    {
        string trimmed = token.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        int open = trimmed.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            specs.Add(new ToolRuleSpec(trimmed, null));
            return;
        }

        if (!trimmed.EndsWith(')') || open == 0)
        {
            throw new ArgumentException($"Invalid tool rule '{trimmed}'. Use name or name(pattern).");
        }

        string tool = trimmed[..open].Trim();
        string pattern = trimmed[(open + 1)..^1].Trim();
        specs.Add(new ToolRuleSpec(tool, pattern.Length == 0 || pattern == "*" ? null : pattern));
    }
}
