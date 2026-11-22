using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Permissions;
using System.Text.Json;

namespace StemCode.Application.Tools.Services;

internal sealed class ToolRegistry : IToolRegistry
{
    private sealed record Snapshot(
        IReadOnlyList<ToolDefinition> Definitions,
        IReadOnlyDictionary<string, ToolRegistration> Tools,
        string VersionKey);

    private readonly IReadOnlyList<ITool> _staticTools;
    private readonly ToolPermissionParser _permissionParser;
    private readonly IReadOnlyList<IDynamicToolProvider> _dynamicToolProviders;
    private readonly object _gate = new();
    private Snapshot _snapshot;

    public ToolRegistry(
        IEnumerable<ITool> tools,
        ToolPermissionParser permissionParser,
        IEnumerable<IDynamicToolProvider>? dynamicToolProviders = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(permissionParser);

        _staticTools = tools.ToArray();
        _permissionParser = permissionParser;
        _dynamicToolProviders = dynamicToolProviders?.ToArray() ?? [];
        _snapshot = Build();
    }

    public IReadOnlyList<ToolDefinition> GetToolDefinitions()
    {
        return GetCurrentSnapshot().Definitions;
    }

    public IReadOnlyList<string> GetRegisteredToolNames()
    {
        return GetCurrentSnapshot().Definitions
            .Select(static definition => definition.Name)
            .ToArray();
    }

    public bool TryResolve(string toolName, out ToolRegistration? tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        return GetCurrentSnapshot().Tools.TryGetValue(toolName.Trim(), out tool);
    }

    // Dynamic providers (MCP, custom tools) can change their tool set at runtime,
    // for example after an MCP server is signed in; rebuild when any version moves.
    private Snapshot GetCurrentSnapshot()
    {
        Snapshot snapshot = _snapshot;
        if (string.Equals(snapshot.VersionKey, ComputeVersionKey(), StringComparison.Ordinal))
        {
            return snapshot;
        }

        lock (_gate)
        {
            if (!string.Equals(_snapshot.VersionKey, ComputeVersionKey(), StringComparison.Ordinal))
            {
                _snapshot = Build();
            }

            return _snapshot;
        }
    }

    private string ComputeVersionKey()
    {
        return _dynamicToolProviders.Count == 0
            ? string.Empty
            : string.Join(",", _dynamicToolProviders.Select(static provider => provider.Version));
    }

    private Snapshot Build()
    {
        string versionKey = ComputeVersionKey();
        Dictionary<string, ToolRegistration> toolMap = new(StringComparer.Ordinal);
        List<ToolDefinition> definitions = [];

        foreach (ITool tool in _staticTools.Concat(_dynamicToolProviders.SelectMany(static provider => provider.GetTools())))
        {
            if (string.IsNullOrWhiteSpace(tool.Description))
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.Name}' must provide a description.");
            }

            ToolDefinition definition = new(
                tool.Name,
                tool.Description,
                ParseSchema(tool));

            ToolPermissionPolicy permissionPolicy = _permissionParser.Parse(
                tool.Name,
                tool.PermissionRequirements);

            if (!toolMap.TryAdd(tool.Name, new ToolRegistration(tool, permissionPolicy)))
            {
                throw new InvalidOperationException(
                    $"Duplicate tool registration detected for '{tool.Name}'.");
            }

            definitions.Add(definition);
        }

        return new Snapshot(
            definitions
                .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
                .ToArray(),
            toolMap,
            versionKey);
    }

    private static JsonElement ParseSchema(ITool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Schema))
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' must provide a JSON schema.");
        }

        using JsonDocument schemaDocument = JsonDocument.Parse(tool.Schema);
        if (schemaDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Tool '{tool.Name}' must provide a JSON-object schema.");
        }

        return schemaDocument.RootElement.Clone();
    }
}
