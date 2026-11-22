using System.Text;

namespace StemCode.Application.Abstractions;

/// <summary>
/// Prompts published by connected MCP servers, exposed as slash commands named
/// <c>/mcp__&lt;server&gt;__&lt;prompt&gt;</c>.
/// </summary>
public interface IMcpPromptService
{
    IReadOnlyList<McpPromptDescriptor> ListPrompts();

    /// <summary>
    /// Expands an <c>/mcp__server__prompt [args]</c> input into the prompt text
    /// returned by the server. Returns null when the input does not name a known
    /// MCP prompt.
    /// </summary>
    Task<McpPromptExpansion?> TryExpandAsync(
        string input,
        CancellationToken cancellationToken);
}

public sealed record McpPromptArgumentDescriptor(
    string Name,
    string? Description,
    bool Required);

public sealed record McpPromptDescriptor(
    string CommandName,
    string ServerName,
    string PromptName,
    string? Description,
    IReadOnlyList<McpPromptArgumentDescriptor> Arguments)
{
    public string Usage
    {
        get
        {
            StringBuilder builder = new("/" + CommandName);
            foreach (McpPromptArgumentDescriptor argument in Arguments)
            {
                builder.Append(argument.Required ? $" <{argument.Name}>" : $" [{argument.Name}]");
            }

            return builder.ToString();
        }
    }
}

/// <param name="Prompt">The expanded prompt text, or null when expansion failed.</param>
/// <param name="Error">Why expansion failed (missing arguments, server error).</param>
public sealed record McpPromptExpansion(
    string? Prompt,
    string? Error);

public static class McpPromptCommand
{
    public const string Prefix = "mcp__";

    /// <summary>True when the input looks like <c>/mcp__…</c>, i.e. it should be sent as a turn.</summary>
    public static bool IsCandidate(string? input)
    {
        return input is not null &&
            input.TrimStart().StartsWith("/" + Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Splits <c>/name rest</c> into the command name (without slash) and its argument text.
    /// </summary>
    public static (string CommandName, string ArgumentText) Split(string input)
    {
        string trimmed = input.Trim().TrimStart('/');
        int space = trimmed.IndexOfAny([' ', '\t', '\n', '\r']);
        return space < 0
            ? (trimmed, string.Empty)
            : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    /// <summary>
    /// Maps argument text onto a prompt's declared arguments. Tokens may be quoted;
    /// <c>name=value</c> sets a named argument, other tokens fill the remaining
    /// arguments in order, and extra tokens are appended to the last argument.
    /// </summary>
    public static bool TryBindArguments(
        McpPromptDescriptor prompt,
        string argumentText,
        out Dictionary<string, string> values,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        Dictionary<string, string> bound = new(StringComparer.Ordinal);
        values = bound;
        error = null;
        List<string> positional = [];

        foreach (string token in Tokenize(argumentText))
        {
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                string name = token[..equals];
                McpPromptArgumentDescriptor? match = prompt.Arguments.FirstOrDefault(argument =>
                    string.Equals(argument.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    values[match.Name] = token[(equals + 1)..];
                    continue;
                }
            }

            positional.Add(token);
        }

        McpPromptArgumentDescriptor[] unfilled = prompt.Arguments
            .Where(argument => !bound.ContainsKey(argument.Name))
            .ToArray();
        for (int index = 0; index < positional.Count; index++)
        {
            if (unfilled.Length == 0)
            {
                error = $"/{prompt.CommandName} takes no more arguments. Usage: {prompt.Usage}";
                return false;
            }

            if (index < unfilled.Length)
            {
                values[unfilled[index].Name] = positional[index];
            }
            else
            {
                string last = unfilled[^1].Name;
                values[last] = values[last] + " " + positional[index];
            }
        }

        string[] missing = prompt.Arguments
            .Where(argument => argument.Required && !bound.ContainsKey(argument.Name))
            .Select(static argument => argument.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            error = $"Missing required argument(s) {string.Join(", ", missing)}. Usage: {prompt.Usage}";
            return false;
        }

        return true;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        StringBuilder current = new();
        char? quote = null;
        bool hasToken = false;
        foreach (char character in text ?? string.Empty)
        {
            if (quote is not null)
            {
                if (character == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (hasToken)
                {
                    yield return current.ToString();
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(character);
            hasToken = true;
        }

        if (hasToken)
        {
            yield return current.ToString();
        }
    }
}
