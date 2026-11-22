using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Application.UI;
using System.Text;
using System.Text.Json;

namespace StemCode.CLI;

/// <summary>
/// UI bridge for <c>--output-format stream-json</c>: every progress event of a
/// one-shot run is written to stdout as one JSON object per line (NDJSON), so CI
/// jobs and other programs can follow a run live. Interactive prompts and
/// human-readable status still go to stderr through the wrapped
/// <see cref="ConsoleBridge"/>.
/// </summary>
internal sealed class StreamJsonBridge : IUiBridge
{
    private readonly IUiBridge _inner;
    private readonly TextWriter _output;
    private readonly object _writeLock = new();

    public StreamJsonBridge(IUiBridge inner, TextWriter output)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public Task<T> RequestSelectionAsync<T>(
        SelectionPromptRequest<T> request,
        CancellationToken cancellationToken)
    {
        return _inner.RequestSelectionAsync(request, cancellationToken);
    }

    public Task<string> RequestTextAsync(
        TextPromptRequest request,
        bool isSecret,
        CancellationToken cancellationToken)
    {
        return _inner.RequestTextAsync(request, isSecret, cancellationToken);
    }

    public void ShowError(string message)
    {
        WriteSystem("error", message);
    }

    public void ShowInfo(string message)
    {
        WriteSystem("info", message);
    }

    public void ShowSuccess(string message)
    {
        WriteSystem("success", message);
    }

    public void ShowAssistantMessageChunk(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        WriteEvent("assistant_delta", writer => writer.WriteString("text", text));
    }

    public void ShowAssistantReasoning(string reasoningText)
    {
        if (string.IsNullOrWhiteSpace(reasoningText))
        {
            return;
        }

        WriteEvent("reasoning", writer => writer.WriteString("text", reasoningText.Trim()));
    }

    public void ShowToolCalls(IReadOnlyList<ConversationToolCall> toolCalls)
    {
        foreach (ConversationToolCall toolCall in toolCalls)
        {
            WriteEvent("tool_use", writer =>
            {
                writer.WriteString("id", toolCall.Id);
                writer.WriteString("name", toolCall.Name);
                writer.WritePropertyName("input");
                WriteJsonOrString(writer, toolCall.ArgumentsJson);
            });
        }
    }

    public void ShowToolResults(ToolExecutionBatchResult toolExecutionResult)
    {
        foreach (ToolInvocationResult result in toolExecutionResult.Results)
        {
            WriteEvent("tool_result", writer =>
            {
                writer.WriteString("id", result.ToolCallId);
                writer.WriteString("name", result.ToolName);
                writer.WriteString("status", ToSnakeCase(result.Result.Status.ToString()));
                writer.WriteBoolean("isError", result.Result.Status != ToolResultStatus.Success);
                writer.WriteString("message", result.Result.Message);
                writer.WritePropertyName("result");
                WriteJsonOrString(writer, result.Result.JsonResult);
            });
        }
    }

    public void ShowExecutionPlan(ExecutionPlanProgress progress)
    {
        WriteEvent("plan", writer =>
        {
            writer.WriteNumber("completed", progress.CompletedTaskCount);
            writer.WriteNumber("total", progress.Tasks.Count);
            writer.WritePropertyName("tasks");
            writer.WriteStartArray();
            for (int index = 0; index < progress.Tasks.Count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString("task", progress.Tasks[index]);
                writer.WriteString(
                    "status",
                    index < progress.CompletedTaskCount
                        ? "completed"
                        : index == progress.CurrentTaskIndex ? "in_progress" : "pending");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    public void ShowProviderRetry(ProviderRetryProgress progress)
    {
        WriteEvent("retry", writer =>
        {
            writer.WriteNumber("attempt", progress.Attempt);
            writer.WriteNumber("maxAttempts", progress.MaxAttempts);
            writer.WriteString("reason", progress.Reason);
        });
    }

    public void WriteInit(BackendSessionInfo sessionInfo)
    {
        WriteLine(CliJsonOutputWriter.FormatStreamInit(sessionInfo));
    }

    public void WriteLine(string json)
    {
        lock (_writeLock)
        {
            _output.WriteLine(json);
            _output.Flush();
        }
    }

    private void WriteSystem(string subtype, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        WriteEvent("system", writer =>
        {
            writer.WriteString("subtype", subtype);
            writer.WriteString("message", message.Trim());
        });
    }

    private void WriteEvent(string type, Action<Utf8JsonWriter> writeProperties)
    {
        WriteLine(CliJsonOutputWriter.FormatStreamEvent(type, writeProperties));
    }

    private static void WriteJsonOrString(Utf8JsonWriter writer, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }

    private static string ToSnakeCase(string value)
    {
        StringBuilder builder = new(value.Length + 4);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
