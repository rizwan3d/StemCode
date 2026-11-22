namespace StemCode.Application.Abstractions;

public interface IDynamicToolProvider
{
    IReadOnlyList<ITool> GetTools();

    IReadOnlyList<DynamicToolProviderStatus> GetStatuses();

    /// <summary>
    /// Changes whenever the provider's tool set changes (for example after an MCP
    /// server is signed in and reconnected), so the tool registry knows to rebuild.
    /// </summary>
    int Version => 0;
}

public sealed record DynamicToolProviderStatus(
    string Name,
    string Kind,
    bool Enabled,
    bool IsAvailable,
    int ToolCount,
    string? Details);
