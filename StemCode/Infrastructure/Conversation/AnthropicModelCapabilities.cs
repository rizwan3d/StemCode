using System.Globalization;
using System.Text.RegularExpressions;

namespace StemCode.Infrastructure.Conversation;

internal enum AnthropicThinkingSupport
{
    // No extended thinking (Claude 3 / 3.5). The thinking parameter must be omitted.
    None,

    // thinking: { type: "enabled", budget_tokens: N } (Claude 3.7 through the 4.5 generation).
    Manual,

    // thinking: { type: "adaptive" } plus output_config.effort (Claude 4.6 and later).
    Adaptive
}

/// <summary>
/// Per-model request limits for the Anthropic Messages API. Newer models reject
/// <c>budget_tokens</c>, some reject <c>thinking: disabled</c>, and older models cap
/// <c>max_tokens</c> well below current ones, so a single request shape does not fit every model.
/// </summary>
internal sealed partial record AnthropicModelCapabilities(
    AnthropicThinkingSupport Thinking,
    bool CanDisableThinking,
    bool SupportsXHighEffort,
    int MaxOutputTokens)
{
    // Requests stream, so a large ceiling does not risk HTTP timeouts. Current models allow
    // up to 128K; 64K leaves room for long file writes without inviting runaway output.
    internal const int DefaultMaxOutputTokens = 64_000;

    private static readonly AnthropicModelCapabilities Current = new(
        AnthropicThinkingSupport.Adaptive,
        CanDisableThinking: true,
        SupportsXHighEffort: true,
        DefaultMaxOutputTokens);

    public static AnthropicModelCapabilities Resolve(string? modelId)
    {
        string normalized = (modelId ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized.Contains("fable", StringComparison.Ordinal) ||
            normalized.Contains("mythos", StringComparison.Ordinal))
        {
            return Current with { CanDisableThinking = false };
        }

        Match modern = ModernModelPattern().Match(normalized);
        if (modern.Success)
        {
            return ResolveVersioned(
                modern.Groups["family"].Value,
                ParseInt(modern.Groups["major"].Value),
                ParseInt(modern.Groups["minor"].Value));
        }

        Match legacy = LegacyModelPattern().Match(normalized);
        if (legacy.Success)
        {
            return ResolveVersioned(
                legacy.Groups["family"].Value,
                ParseInt(legacy.Groups["major"].Value),
                ParseInt(legacy.Groups["minor"].Value));
        }

        // Unrecognized ids are most likely newer than this table. Treat them as current, but
        // leave thinking on when the user turns it off: omitting the parameter is valid on
        // every adaptive model, while thinking: disabled is rejected by several of them.
        return Current with { CanDisableThinking = false };
    }

    private static AnthropicModelCapabilities ResolveVersioned(string family, int major, int minor)
    {
        if (major < 3 || (major == 3 && minor < 7))
        {
            return new AnthropicModelCapabilities(
                AnthropicThinkingSupport.None,
                CanDisableThinking: false,
                SupportsXHighEffort: false,
                MaxOutputTokens: major == 3 && minor >= 5 ? 8_192 : 4_096);
        }

        if (major == 3 || (major == 4 && minor < 6))
        {
            bool isOpus40Or41 = family == "opus" && major == 4 && minor <= 1;
            return new AnthropicModelCapabilities(
                AnthropicThinkingSupport.Manual,
                CanDisableThinking: true,
                SupportsXHighEffort: false,
                MaxOutputTokens: isOpus40Or41 ? 32_000 : DefaultMaxOutputTokens);
        }

        // Opus and Sonnet 5.5+ reject thinking: disabled at every effort level.
        bool canDisable = family == "haiku" || major < 5 || (major == 5 && minor < 5);
        return Current with
        {
            CanDisableThinking = canDisable,
            SupportsXHighEffort = !(major == 4 && minor == 6)
        };
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }

    // claude-opus-4-1, claude-sonnet-4-5-20250929, claude-opus-5-5, claude-opus-4-20250514.
    // The minor version is one or two digits; an eight-digit date suffix is not a minor version.
    [GeneratedRegex(
        @"claude-(?<family>opus|sonnet|haiku)-(?<major>\d{1,2})(?:-(?<minor>\d{1,2})(?!\d))?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ModernModelPattern();

    // claude-3-7-sonnet-latest, claude-3-5-haiku-20241022, claude-3-opus-20240229.
    [GeneratedRegex(
        @"claude-(?<major>\d)(?:-(?<minor>\d))?-(?<family>opus|sonnet|haiku)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LegacyModelPattern();
}
