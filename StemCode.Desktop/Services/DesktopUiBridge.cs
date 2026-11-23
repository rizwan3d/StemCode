using StemCode.Application.Exceptions;
using StemCode.Application.Formatting;
using StemCode.Application.Models;
using StemCode.Application.UI;
using StemCode.Desktop.Models;

namespace StemCode.Desktop.Services;

/// <summary>Receives structured agent output for the live transcript. Calls arrive on worker threads.</summary>
public interface IDesktopTranscriptSink
{
    void OnAssistantChunk(string text);

    void OnReasoning(string text);

    void OnToolCallsStarted(IReadOnlyList<DesktopToolCall> calls);

    void OnToolCompleted(DesktopToolResult result);

    void OnPlan(ExecutionPlanProgress progress);

    void OnNotice(DesktopNoticeLevel level, string message);

    void OnProviderRetry(ProviderRetryProgress progress);
}

public enum DesktopNoticeLevel
{
    Info,
    Success,
    Error
}

public sealed record DesktopToolCall(string Id, string Name, string Title);

public sealed record DesktopToolResult(string Id, string Name, string Text, bool Succeeded, bool IsPlanUpdate);

internal sealed class DesktopUiBridge : IUiBridge
{
    private readonly IDesktopTranscriptSink? _sink;
    private readonly Action<DesktopSelectionPrompt?> _setSelectionPrompt;
    private readonly Action<DesktopTextPrompt?> _setTextPrompt;
    private readonly IToolOutputFormatter _toolOutputFormatter;
    private readonly Action<string> _addActivity;
    private DesktopSelectionPrompt? _currentSelectionPrompt;
    private DesktopTextPrompt? _currentTextPrompt;

    public DesktopUiBridge(
        Action<string> addActivity,
        IDesktopTranscriptSink? sink,
        Action<DesktopSelectionPrompt?> setSelectionPrompt,
        Action<DesktopTextPrompt?> setTextPrompt)
    {
        _addActivity = addActivity ?? throw new ArgumentNullException(nameof(addActivity));
        _sink = sink;
        _setSelectionPrompt = setSelectionPrompt ?? throw new ArgumentNullException(nameof(setSelectionPrompt));
        _setTextPrompt = setTextPrompt ?? throw new ArgumentNullException(nameof(setTextPrompt));
        _toolOutputFormatter = new ToolOutputFormatter();
    }

    public async Task<T> RequestSelectionAsync<T>(
        SelectionPromptRequest<T> request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Options.Count == 0)
        {
            throw new InvalidOperationException("StemCode requested a selection with no available options.");
        }

        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DesktopSelectionPrompt? prompt = null;

        prompt = new DesktopSelectionPrompt(
            request.Title,
            request.Description,
            request.Options
                .Select(static option => new DesktopSelectionPromptOptionDescriptor(
                    option.Label,
                    option.Description,
                    option.Section))
                .ToArray(),
            request.DefaultIndex,
            request.AllowCancellation,
            request.AutoSelectAfter,
            onSelected: (selectedIndex, isAutomatic) =>
            {
                SelectionPromptOption<T> selectedOption = request.Options[selectedIndex];
                string prefix = isAutomatic ? "Auto-selected" : "Selected";
                AddActivity($"{prefix} '{selectedOption.Label}' for: {request.Title}");
                completion.TrySetResult(selectedOption.Value);
            },
            onCancelled: () =>
            {
                AddActivity($"Cancelled prompt: {request.Title}");
                completion.TrySetException(new PromptCancelledException());
            });

        prompt.Dismissed += (_, _) => ClearSelectionPrompt(prompt);
        SetSelectionPrompt(prompt);

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            prompt.Dismiss();
            completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            ClearSelectionPrompt(prompt);
        }
    }

    public async Task<string> RequestTextAsync(
        TextPromptRequest request,
        bool isSecret,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DesktopTextPrompt? prompt = null;

        prompt = new DesktopTextPrompt(
            request.Label,
            request.Description,
            request.DefaultValue,
            request.AllowCancellation,
            isSecret,
            onSubmitted: value =>
            {
                AddActivity($"Submitted prompt: {request.Label}");
                completion.TrySetResult(value);
            },
            onCancelled: () =>
            {
                AddActivity($"Cancelled prompt: {request.Label}");
                completion.TrySetException(new PromptCancelledException());
            });

        prompt.Dismissed += (_, _) => ClearTextPrompt(prompt);
        SetTextPrompt(prompt);

        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            prompt.Dismiss();
            completion.TrySetCanceled(cancellationToken);
        });

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            ClearTextPrompt(prompt);
        }
    }

    public void ShowError(string message)
    {
        AddActivity($"Error: {message}");
        _sink?.OnNotice(DesktopNoticeLevel.Error, message);
    }

    public void ShowInfo(string message)
    {
        AddActivity(message);
        _sink?.OnNotice(DesktopNoticeLevel.Info, message);
    }

    public void ShowSuccess(string message)
    {
        AddActivity($"Success: {message}");
        _sink?.OnNotice(DesktopNoticeLevel.Success, message);
    }

    public void ShowAssistantMessageChunk(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            _sink?.OnAssistantChunk(text);
        }
    }

    public void ShowAssistantReasoning(string reasoningText)
    {
        if (string.IsNullOrWhiteSpace(reasoningText))
        {
            return;
        }

        AddActivity("Thinking");
        _sink?.OnReasoning(reasoningText.Trim());
    }

    public void ShowToolCalls(IReadOnlyList<ConversationToolCall> toolCalls)
    {
        DesktopToolCall[] calls = toolCalls
            .Select(call =>
            {
                string description = _toolOutputFormatter.DescribeCall(call);
                return new DesktopToolCall(
                    call.Id,
                    call.Name,
                    string.IsNullOrWhiteSpace(description) ? call.Name : description.Trim());
            })
            .ToArray();

        foreach (DesktopToolCall call in calls)
        {
            AddActivity($"Running {call.Title}");
        }

        if (calls.Length > 0)
        {
            _sink?.OnToolCallsStarted(calls);
        }
    }

    public void ShowToolResults(ToolExecutionBatchResult toolExecutionResult)
    {
        foreach (ToolInvocationResult result in toolExecutionResult.Results)
        {
            IReadOnlyList<string> formatted = _toolOutputFormatter.FormatResults(
                new ToolExecutionBatchResult([result]));
            string text = string.Join(Environment.NewLine + Environment.NewLine, formatted).Trim();
            bool isPlanUpdate = formatted.Count == 0 && result.Result.IsSuccess;
            if (!isPlanUpdate)
            {
                AddActivity(FirstLine(text, result.ToolName));
            }

            _sink?.OnToolCompleted(new DesktopToolResult(
                result.ToolCallId,
                result.ToolName,
                text,
                result.Result.IsSuccess,
                isPlanUpdate));
        }
    }

    public void ShowExecutionPlan(ExecutionPlanProgress progress)
    {
        AddActivity($"Plan: {progress.CompletedTaskCount}/{progress.Tasks.Count} done");
        _sink?.OnPlan(progress);
    }

    public void ShowProviderRetry(ProviderRetryProgress progress)
    {
        AddActivity($"Retrying request {progress.Attempt}/{progress.MaxAttempts}: {progress.Reason}");
        _sink?.OnProviderRetry(progress);
    }

    private static string FirstLine(string text, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        int newline = text.IndexOf('\n');
        return (newline < 0 ? text : text[..newline]).Trim();
    }

    private void AddActivity(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            _addActivity(message.Trim());
        }
    }

    private void SetSelectionPrompt(DesktopSelectionPrompt prompt)
    {
        _currentSelectionPrompt = prompt;
        _setSelectionPrompt(prompt);
    }

    private void ClearSelectionPrompt(DesktopSelectionPrompt prompt)
    {
        if (!ReferenceEquals(_currentSelectionPrompt, prompt))
        {
            return;
        }

        _currentSelectionPrompt = null;
        _setSelectionPrompt(null);
    }

    private void SetTextPrompt(DesktopTextPrompt prompt)
    {
        _currentTextPrompt = prompt;
        _setTextPrompt(prompt);
    }

    private void ClearTextPrompt(DesktopTextPrompt prompt)
    {
        if (!ReferenceEquals(_currentTextPrompt, prompt))
        {
            return;
        }

        _currentTextPrompt = null;
        _setTextPrompt(null);
    }
}
