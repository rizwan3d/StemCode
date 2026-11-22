using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools;

namespace StemCode.Application.Commands;

internal sealed class McpCommandHandler : IReplCommandHandler
{
    private readonly IEnumerable<IDynamicToolProvider> _dynamicToolProviders;
    private readonly IToolRegistry _toolRegistry;
    private readonly IMcpPromptService? _mcpPromptService;

    public McpCommandHandler(
        IEnumerable<IDynamicToolProvider> dynamicToolProviders,
        IToolRegistry toolRegistry,
        IMcpPromptService? mcpPromptService = null)
    {
        _dynamicToolProviders = dynamicToolProviders;
        _toolRegistry = toolRegistry;
        _mcpPromptService = mcpPromptService;
    }

    public string CommandName => "mcp";

    public string Description => "Show configured MCP servers, custom tool providers, and discovered dynamic tools.";

    public string Usage => "/mcp";

    public Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

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

        return Task.FromResult(ReplCommandResult.Continue(string.Join(Environment.NewLine, lines)));
    }
}
