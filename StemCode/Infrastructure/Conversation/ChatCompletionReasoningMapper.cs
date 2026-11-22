using StemCode.Application.Models;
using StemCode.Domain.Models;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StemCode.Infrastructure.Conversation;

internal enum ReasoningModelFamily
{
    Unknown,
    OpenAiReasoning,
    OpenAiNonReasoning,
    GptOss,
    DeepSeek,
    Glm,
    Kimi,
    Qwen
}

/// <summary>
/// Reasoning parameters to put on an OpenAI-compatible chat completion request.
/// Any field left null is omitted from the payload.
/// </summary>
internal sealed record ChatCompletionReasoningParameters(
    string? ReasoningEffort = null,
    ChatCompletionThinkingConfig? Thinking = null,
    bool? EnableThinking = null)
{
    public static ChatCompletionReasoningParameters Empty { get; } = new();
}

/// <summary>
/// Maps StemCode's normalized thinking level (none..max) onto what each model family / host
/// actually accepts on the chat completions endpoint.
/// </summary>
internal static partial class ChatCompletionReasoningMapper
{
    private const string Low = ReasoningEffortOptions.Low;
    private const string Medium = ReasoningEffortOptions.Medium;
    private const string High = ReasoningEffortOptions.High;
    private const string XHigh = ReasoningEffortOptions.XHigh;
    private const string Max = ReasoningEffortOptions.Max;
    private const string None = ReasoningEffortOptions.None;
    private const string Minimal = ReasoningEffortOptions.Minimal;

    /// <param name="thinkingEnabled">Whether thinking is switched on for the session.</param>
    /// <param name="effort">Normalized effort (none, minimal, low, medium, high, xhigh, max).</param>
    public static ChatCompletionReasoningParameters Map(
        ProviderKind providerKind,
        string modelId,
        bool thinkingEnabled,
        string effort)
    {
        // An explicit "none" effort means the same thing as switching thinking off.
        bool enabled = thinkingEnabled && effort != None;
        string model = NormalizeModelId(modelId);
        ReasoningModelFamily family = DetectFamily(model);

        return providerKind switch
        {
            ProviderKind.DeepSeek => MapDeepSeek(enabled, effort),
            ProviderKind.Cerebras => MapCerebras(family, enabled, effort),
            ProviderKind.Groq => MapGroq(family, enabled, effort),
            _ => MapByFamily(providerKind, family, model, enabled, effort)
        };
    }

    public static ReasoningModelFamily DetectFamily(string modelId)
    {
        string model = NormalizeModelId(modelId);

        if (model.Contains("gpt-oss", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.GptOss;
        }

        if (model.StartsWith("deepseek", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.DeepSeek;
        }

        if (model.StartsWith("glm", StringComparison.Ordinal) ||
            model.StartsWith("zai-glm", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.Glm;
        }

        if (model.StartsWith("kimi", StringComparison.Ordinal) ||
            model.StartsWith("moonshot", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.Kimi;
        }

        if (model.StartsWith("qwen", StringComparison.Ordinal) ||
            model.StartsWith("qwq", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.Qwen;
        }

        if (model.StartsWith("gpt-5", StringComparison.Ordinal) ||
            model.StartsWith("codex", StringComparison.Ordinal) ||
            OpenAiOSeriesRegex().IsMatch(model))
        {
            return ReasoningModelFamily.OpenAiReasoning;
        }

        if (model.StartsWith("gpt-4", StringComparison.Ordinal) ||
            model.StartsWith("gpt-3.5", StringComparison.Ordinal) ||
            model.StartsWith("chatgpt-4o", StringComparison.Ordinal))
        {
            return ReasoningModelFamily.OpenAiNonReasoning;
        }

        return ReasoningModelFamily.Unknown;
    }

    private static ChatCompletionReasoningParameters MapByFamily(
        ProviderKind providerKind,
        ReasoningModelFamily family,
        string model,
        bool enabled,
        string effort)
    {
        return family switch
        {
            ReasoningModelFamily.OpenAiReasoning => MapOpenAiReasoning(model, enabled, effort),
            ReasoningModelFamily.OpenAiNonReasoning => ChatCompletionReasoningParameters.Empty,
            ReasoningModelFamily.GptOss => new(ReasoningEffort: ToLowMediumHigh(enabled ? effort : Low)),
            ReasoningModelFamily.Glm => MapGlm(model, enabled, effort),
            ReasoningModelFamily.Kimi => MapKimi(model, enabled, effort),
            ReasoningModelFamily.Qwen => MapQwen(model, enabled),
            // DeepSeek weights served by a third-party host (R1/V3 distills etc.) do not
            // understand DeepSeek's native effort scale, so treat them like any other model.
            _ => MapUnknown(providerKind, enabled, effort)
        };
    }

    // OpenAI reasoning models:
    //   o-series:      low | medium | high (cannot be disabled)
    //   gpt-5 (5.0):   minimal | low | medium | high
    //   gpt-5.1+:      none | low | medium | high (+ xhigh on 5.2+ and *-codex-max)
    private static ChatCompletionReasoningParameters MapOpenAiReasoning(string model, bool enabled, string effort)
    {
        Match gpt5 = Gpt5VersionRegex().Match(model);
        if (!gpt5.Success)
        {
            return new(ReasoningEffort: ToLowMediumHigh(enabled ? effort : Low));
        }

        int minor = gpt5.Groups[1].Success
            ? int.Parse(gpt5.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
        bool supportsNone = minor >= 1;
        bool supportsXHigh = minor >= 2 || model.Contains("codex-max", StringComparison.Ordinal);

        if (!enabled)
        {
            return new(ReasoningEffort: supportsNone ? None : Minimal);
        }

        string mapped = effort switch
        {
            Minimal => supportsNone ? Low : Minimal,
            Low or Medium or High => effort,
            XHigh or Max => supportsXHigh ? XHigh : High,
            _ => Medium
        };

        return new(ReasoningEffort: mapped);
    }

    // DeepSeek native API: reasoning_effort none | low | high | max, plus thinking.type.
    private static ChatCompletionReasoningParameters MapDeepSeek(bool enabled, string effort)
    {
        if (!enabled)
        {
            return new(ReasoningEffort: None, Thinking: ChatCompletionThinkingConfig.Disabled);
        }

        string mapped = effort switch
        {
            Minimal or Low => Low,
            XHigh or Max => Max,
            _ => High
        };

        return new(ReasoningEffort: mapped, Thinking: ChatCompletionThinkingConfig.Enabled);
    }

    // Z.ai GLM: thinking.type enabled | disabled. reasoning_effort low | high | max only on
    // GLM-5.2+. GLM-5.3 rejects thinking.type = disabled.
    private static ChatCompletionReasoningParameters MapGlm(string model, bool enabled, string effort)
    {
        (int major, int minor) = ParseVersion(GlmVersionRegex().Match(model));
        bool supportsEffort = major > 5 || (major == 5 && minor >= 2);
        bool canDisable = !(major > 5 || (major == 5 && minor >= 3));

        if (!enabled)
        {
            return canDisable
                ? new(Thinking: ChatCompletionThinkingConfig.Disabled)
                : supportsEffort ? new(ReasoningEffort: Low) : ChatCompletionReasoningParameters.Empty;
        }

        string? mapped = supportsEffort
            ? effort switch
            {
                Minimal or Low => Low,
                XHigh or Max => Max,
                _ => High
            }
            : null;

        return new(ReasoningEffort: mapped, Thinking: ChatCompletionThinkingConfig.Enabled);
    }

    // Kimi: K2.5/K2.6 use thinking.type; K3 always thinks and only takes reasoning_effort;
    // older K2 checkpoints (instruct / *-thinking) have a fixed mode.
    private static ChatCompletionReasoningParameters MapKimi(string model, bool enabled, string effort)
    {
        (int major, int minor) = ParseVersion(KimiVersionRegex().Match(model));

        if (major >= 3)
        {
            return new(ReasoningEffort: ToLowMediumHigh(enabled ? effort : Low));
        }

        if (major == 2 && minor >= 5)
        {
            return new(Thinking: enabled
                ? ChatCompletionThinkingConfig.Enabled
                : ChatCompletionThinkingConfig.Disabled);
        }

        return ChatCompletionReasoningParameters.Empty;
    }

    // Qwen hybrid models (DashScope, vLLM, SGLang) toggle with enable_thinking. The
    // *-instruct / *-thinking checkpoints have a fixed mode and must not receive it.
    private static ChatCompletionReasoningParameters MapQwen(string model, bool enabled)
    {
        if (model.Contains("instruct", StringComparison.Ordinal) ||
            model.Contains("thinking", StringComparison.Ordinal) ||
            model.StartsWith("qwq", StringComparison.Ordinal) ||
            model.Contains("coder", StringComparison.Ordinal))
        {
            return ChatCompletionReasoningParameters.Empty;
        }

        return new(EnableThinking: enabled);
    }

    // Cerebras: reasoning_effort only. gpt-oss takes low | medium | high and cannot be
    // disabled; GLM / Qwen take "none" to disable and think by default otherwise.
    private static ChatCompletionReasoningParameters MapCerebras(
        ReasoningModelFamily family,
        bool enabled,
        string effort)
    {
        return family switch
        {
            ReasoningModelFamily.GptOss => new(ReasoningEffort: ToLowMediumHigh(enabled ? effort : Low)),
            ReasoningModelFamily.Glm or ReasoningModelFamily.Qwen => enabled
                ? ChatCompletionReasoningParameters.Empty
                : new(ReasoningEffort: None),
            _ => ChatCompletionReasoningParameters.Empty
        };
    }

    // Groq: gpt-oss takes low | medium | high; Qwen takes none | default. Other models
    // (Llama, Kimi instruct, ...) reject the field.
    private static ChatCompletionReasoningParameters MapGroq(
        ReasoningModelFamily family,
        bool enabled,
        string effort)
    {
        return family switch
        {
            ReasoningModelFamily.GptOss => new(ReasoningEffort: ToLowMediumHigh(enabled ? effort : Low)),
            ReasoningModelFamily.Qwen => new(ReasoningEffort: enabled ? "default" : None),
            _ => ChatCompletionReasoningParameters.Empty
        };
    }

    // Unknown models: only send an effort when thinking is on. Sending "none" to a model
    // that has no reasoning support is the most common cause of 400s on generic endpoints.
    private static ChatCompletionReasoningParameters MapUnknown(
        ProviderKind providerKind,
        bool enabled,
        string effort)
    {
        if (!enabled)
        {
            return ChatCompletionReasoningParameters.Empty;
        }

        return providerKind == ProviderKind.OpenAi
            ? new(ReasoningEffort: effort == Max ? XHigh : effort)
            : new(ReasoningEffort: ToLowMediumHigh(effort));
    }

    /// <summary>
    /// Ollama native /api/chat "think" value: gpt-oss only accepts levels, everything else a bool.
    /// </summary>
    public static JsonElement MapOllamaThink(string modelId, bool thinkingEnabled, string effort)
    {
        bool enabled = thinkingEnabled && effort != None;
        string json = DetectFamily(modelId) == ReasoningModelFamily.GptOss
            ? $"\"{ToLowMediumHigh(enabled ? effort : Low)}\""
            : enabled ? "true" : "false";

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ToLowMediumHigh(string effort)
    {
        return effort switch
        {
            None or Minimal or Low => Low,
            Medium => Medium,
            High or XHigh or Max => High,
            _ => Medium
        };
    }

    private static string NormalizeModelId(string modelId)
    {
        string model = (modelId ?? string.Empty).Trim().ToLowerInvariant();
        int slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

    private static (int Major, int Minor) ParseVersion(Match match)
    {
        if (!match.Success)
        {
            return (0, 0);
        }

        int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minor = match.Groups[2].Success
            ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
            : 0;
        return (major, minor);
    }

    [GeneratedRegex(@"^o\d")]
    private static partial Regex OpenAiOSeriesRegex();

    [GeneratedRegex(@"^gpt-5(?:\.(\d+))?")]
    private static partial Regex Gpt5VersionRegex();

    [GeneratedRegex(@"glm-(\d+)(?:\.(\d+))?")]
    private static partial Regex GlmVersionRegex();

    [GeneratedRegex(@"^kimi-k(\d+)(?:\.(\d+))?")]
    private static partial Regex KimiVersionRegex();
}
