using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Application.UI;

namespace StemCode.Application.Backend;

public interface IStemCodeBackend : IAsyncDisposable
{
    Task<BackendSessionInfo> InitializeAsync(
        IUiBridge uiBridge,
        CancellationToken cancellationToken);

    Task<BackendCommandResult> RunCommandAsync(
        string commandText,
        CancellationToken cancellationToken);

    Task<BackendCommandResult> SelectModelAsync(
        CancellationToken cancellationToken);

    Task<ConversationTurnResult> RunTurnAsync(
        string input,
        IUiBridge uiBridge,
        CancellationToken cancellationToken);

    Task<ConversationTurnResult> RunTurnAsync(
        string input,
        IReadOnlyList<ConversationAttachment> attachments,
        IUiBridge uiBridge,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BackgroundTerminalInfo>> ListBackgroundTerminalsAsync(
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BackgroundTerminalInfo>>([]);

    Task<ShellCommandExecutionResult> ReadBackgroundTerminalAsync(
        string terminalId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This backend does not support background terminals.");

    /// <summary>Per-file added/removed line totals for the current conversation, for the end-of-conversation summary.</summary>
    IReadOnlyList<FileEditSummary> GetFileEditSummary() => [];

    /// <summary>MCP prompts available as /mcp__server__prompt commands. May connect to MCP servers.</summary>
    IReadOnlyList<Abstractions.McpPromptDescriptor> ListMcpPrompts() => [];
}
