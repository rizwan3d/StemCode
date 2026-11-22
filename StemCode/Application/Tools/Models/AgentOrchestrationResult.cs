namespace StemCode.Application.Tools.Models;

public sealed class AgentOrchestrationResult
{
    public AgentOrchestrationResult(
        string strategy,
        IReadOnlyList<AgentOrchestrationTaskResult> tasks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategy);
        ArgumentNullException.ThrowIfNull(tasks);

        Strategy = strategy.Trim();
        Tasks = tasks.ToArray();
        EstimatedOutputTokens = Tasks.Sum(static task => task.EstimatedOutputTokens);
        RecordedFileEdits = Tasks.Any(static task => task.RecordedFileEdits);
        SucceededTaskCount = Tasks.Count(static task => task.Succeeded);
        FailedTaskCount = Tasks.Count - SucceededTaskCount;
    }

    public int EstimatedOutputTokens { get; }

    public int FailedTaskCount { get; }

    public bool RecordedFileEdits { get; }

    public string Strategy { get; }

    public int SucceededTaskCount { get; }

    public IReadOnlyList<AgentOrchestrationTaskResult> Tasks { get; }
}

public sealed class AgentOrchestrationTaskResult
{
    public AgentOrchestrationTaskResult(
        int index,
        string agentName,
        string task,
        bool succeeded,
        string response,
        IReadOnlyList<string> executedTools,
        int estimatedOutputTokens,
        bool recordedFileEdits,
        string? errorMessage = null,
        AgentWorktreeResult? worktree = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(executedTools);

        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (estimatedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedOutputTokens));
        }

        Index = index;
        AgentName = agentName.Trim();
        Task = task.Trim();
        Succeeded = succeeded;
        Response = string.IsNullOrWhiteSpace(response)
            ? string.Empty
            : response.Trim();
        ExecutedTools = executedTools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Select(static tool => tool.Trim())
            .ToArray();
        EstimatedOutputTokens = estimatedOutputTokens;
        RecordedFileEdits = recordedFileEdits;
        ErrorMessage = string.IsNullOrWhiteSpace(errorMessage)
            ? null
            : errorMessage.Trim();
        Worktree = worktree;
    }

    public string AgentName { get; }

    public string? ErrorMessage { get; }

    public int EstimatedOutputTokens { get; }

    public IReadOnlyList<string> ExecutedTools { get; }

    public int Index { get; }

    public bool RecordedFileEdits { get; }

    public string Response { get; }

    public bool Succeeded { get; }

    public string Task { get; }

    public AgentWorktreeResult? Worktree { get; }
}
