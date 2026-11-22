using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools;

namespace StemCode.Application.Commands;

internal sealed class McpCommandHandler : IReplCommandHandler
{
    private readonly IEnumerable<IDynamicToolProvider> _dynamicToolProviders;
    private readonly IToolRegistry _toolRegistry;
    public const string UsageText = "/mcp [login <server>|logout <server>]";

    private readonly IMcpPromptService? _mcpPromptService;
    private readonly IMcpAuthService? _mcpAuthService;

    public McpCommandHandler(
        IEnumerable<IDynamicToolProvider> dynamicToolProviders,
        IToolRegistry toolRegistry,
        IMcpPromptService? mcpPromptService = null,
        IMcpAuthService? mcpAuthService = null)
    {
        _dynamicToolProviders = dynamicToolProviders;
        _toolRegistry = toolRegistry;
        _mcpPromptService = mcpPromptService;
        _mcpAuthService = mcpAuthService;
    }

    public string CommandName => "mcp";

    public string Description => "Show MCP servers, tools, and prompts, or sign in to and out of remote MCP servers.";

    public string Usage => UsageText;

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Arguments.Count > 0)
        {
            return await ExecuteAuthCommandAsync(context, cancellationToken);
        }

        return ShowStatus();
    }

    private async Task<ReplCommandResult> ExecuteAuthCommandAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        string action = context.Arguments[0].Trim().ToLowerInvariant();
        if (action is not ("login" or "logout") || context.Arguments.Count != 2)
        {
            return ReplCommandResult.Continue($"Usage: {UsageText}", ReplFeedbackKind.Error);
        }

        if (_mcpAuthService is null)
        {
            return ReplCommandResult.Continue("MCP sign-in is not available in this host.", ReplFeedbackKind.Error);
        }

        try
        {
            string message = action == "login"
                ? await _mcpAuthService.LoginAsync(context.Arguments[1], cancellationToken)
                : await _mcpAuthService.LogoutAsync(context.Arguments[1], cancellationToken);
            return ReplCommandResult.Continue(message, ReplFeedbackKind.Info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ReplCommandResult.Continue("MCP sign-in timed out waiting for the browser.", ReplFeedbackKind.Error);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or IOException)
        {
            return ReplCommandResult.Continue(exception.Message, ReplFeedbackKind.Error);
        }
    }

    private ReplCommandResult ShowStatus()
    {

        DynamicToolProviderStatus[] statuses = _dynamicToolProviders
            .SelectMany(static provider => provider.GetStatuses())
            .OrderBy(static status => status.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] toolNames = _toolRegistry.GetToolDefinitions()
            .Select(static definition => definition.Name)
            .Where(static name =>
                name.StartsWith(AgentToolNames.McpToolPrefix, StringComparison.Ordinal) ||
                name.StartsWith(AgentToolNames.CustomToolPrefix, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        List<string> lines = ["Dynamic tool providers:"];
        if (statuses.Length == 0)
        {
            lines.Add("No dynamic tool providers are configured.");
        }
        else
        {
            foreach (DynamicToolProviderStatus status in statuses)
            {
                string state = status.Enabled
                    ? status.IsAvailable ? "available" : "unavailable"
                    : "disabled";
                string details = string.IsNullOrWhiteSpace(status.Details)
                    ? string.Empty
                    : $" - {status.Details}";

                lines.Add(
                    $"{status.Name} ({status.Kind}): {state}, {status.ToolCount} tool(s){details}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("Dynamic tools:");
        lines.AddRange(toolNames.Length == 0
            ? ["No dynamic tools are currently available."]
            : toolNames);

        IReadOnlyList<McpPromptDescriptor> prompts = _mcpPromptService?.ListPrompts() ?? [];
        if (prompts.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("MCP prompts (run as slash commands):");
            foreach (McpPromptDescriptor prompt in prompts)
            {
                string description = string.IsNullOrWhiteSpace(prompt.Description)
                    ? string.Empty
                    : $" - {prompt.Description.Trim()}";
                lines.Add($"{prompt.Usage}{description}");
            }
        }

        return ReplCommandResult.Continue(string.Join(Environment.NewLine, lines));
    }
}
