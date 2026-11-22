namespace StemCode.Application.Tools.Models;

public sealed class AgentDelegationResult
{
    public AgentDelegationResult(
        string agentName,
        string task,
        string response,
        IReadOnlyList<string> executedTools,
        int estimatedOutputTokens,
        bool recordedFileEdits,
        AgentWorktreeResult? worktree = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(response);
        ArgumentNullException.ThrowIfNull(executedTools);

        if (estimatedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedOutputTokens));
        }

        AgentName = agentName.Trim();
        Task = task.Trim();
        Response = response.Trim();
        ExecutedTools = executedTools
            .Where(static tool => !string.IsNullOrWhiteSpace(tool))
            .Select(static tool => tool.Trim())
            .ToArray();
        EstimatedOutputTokens = estimatedOutputTokens;
        RecordedFileEdits = recordedFileEdits;
        Worktree = worktree;
    }

    public string AgentName { get; }

    public int EstimatedOutputTokens { get; }

    public IReadOnlyList<string> ExecutedTools { get; }

    public bool RecordedFileEdits { get; }

    public string Response { get; }

    public string Task { get; }

    public AgentWorktreeResult? Worktree { get; }
}
