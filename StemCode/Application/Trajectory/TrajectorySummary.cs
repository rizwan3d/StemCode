namespace StemCode.Application.Trajectory;

public sealed record TrajectorySummary(
    int EventCount,
    int UserTurnCount,
    int ReasoningCount,
    int AssistantOutputCount,
    int ToolRequestCount,
    int ToolResultCount,
    int PlanCount,
    int FailureCount,
    DateTimeOffset? FirstTimestampUtc,
    DateTimeOffset? LastTimestampUtc,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> WorkingDirectories,
    string Timeline);
