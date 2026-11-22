using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Permissions;
using StemCode.Application.Tools.Services;

namespace StemCode.Tests.Application.Tools.Services;

public sealed class ToolRegistryTests
{
    [Fact]
    public void TryResolve_Should_ReturnRegisteredTool_When_NameExists()
    {
        ToolRegistry sut = new([
            new StubTool("directory_list"),
            new StubTool("file_read")
        ], new ToolPermissionParser());

        bool found = sut.TryResolve("file_read", out ToolRegistration? tool);

        found.Should().BeTrue();
        tool.Should().NotBeNull();
        tool!.Name.Should().Be("file_read");
        tool.PermissionPolicy.FilePaths.Should().ContainSingle();
        sut.GetRegisteredToolNames().Should().Equal("directory_list", "file_read");
        sut.GetToolDefinitions().Select(definition => definition.Name).Should().Equal("directory_list", "file_read");
    }

    [Fact]
    public void Registry_Should_RebuildDynamicTools_When_ProviderVersionChanges()
    {
        MutableDynamicToolProvider provider = new();
        ToolRegistry sut = new([new StubTool("file_read")], new ToolPermissionParser(), [provider]);

        sut.GetRegisteredToolNames().Should().Equal("file_read");
        sut.TryResolve("mcp__remote__search", out _).Should().BeFalse();

        provider.Tools = [new StubTool("mcp__remote__search")];
        sut.GetRegisteredToolNames().Should().Equal("file_read");

        provider.Version++;
        sut.GetRegisteredToolNames().Should().Equal("file_read", "mcp__remote__search");
        sut.TryResolve("mcp__remote__search", out ToolRegistration? tool).Should().BeTrue();
        tool!.Name.Should().Be("mcp__remote__search");
    }

    private sealed class MutableDynamicToolProvider : IDynamicToolProvider
    {
        public IReadOnlyList<ITool> Tools { get; set; } = [];

        public int Version { get; set; }

        public IReadOnlyList<ITool> GetTools() => Tools;

        public IReadOnlyList<DynamicToolProviderStatus> GetStatuses() => [];
    }

    [Fact]
    public void Constructor_Should_Throw_When_DuplicateToolNamesAreRegistered()
    {
        Action action = () => new ToolRegistry([
            new StubTool("directory_list"),
            new StubTool("directory_list")
        ], new ToolPermissionParser());

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*Duplicate tool registration*");
    }

    [Fact]
    public void Constructor_Should_RegisterDynamicTools()
    {
        ToolRegistry sut = new(
            [new StubTool("file_read")],
            new ToolPermissionParser(),
            [new StubDynamicToolProvider([new StubTool("mcp__docs__search")])]);

        sut.GetRegisteredToolNames()
            .Should()
            .Equal("file_read", "mcp__docs__search");
        sut.TryResolve("mcp__docs__search", out ToolRegistration? registration)
            .Should()
            .BeTrue();
        registration!.PermissionPolicy.FilePaths.Should().ContainSingle();
    }

    [Fact]
    public void Constructor_Should_RegisterMcpAndCustomDynamicToolsTogether()
    {
        ToolRegistry sut = new(
            [new StubTool("file_read")],
            new ToolPermissionParser(),
            [
                new StubDynamicToolProvider([new StubTool("mcp__docs__search")]),
                new StubDynamicToolProvider([new StubTool("custom__word_count")])
            ]);

        sut.GetRegisteredToolNames()
            .Should()
            .Equal("custom__word_count", "file_read", "mcp__docs__search");
        sut.TryResolve("mcp__docs__search", out ToolRegistration? mcpRegistration)
            .Should()
            .BeTrue();
        sut.TryResolve("custom__word_count", out ToolRegistration? customRegistration)
            .Should()
            .BeTrue();
        mcpRegistration!.PermissionPolicy.FilePaths.Should().ContainSingle();
        customRegistration!.PermissionPolicy.FilePaths.Should().ContainSingle();
    }

    private sealed class StubTool : ITool
    {
        public StubTool(string name)
        {
            Name = name;
        }

        public string Description => $"Description for {Name}";

        public string Name { get; }

        public string PermissionRequirements => """
            {
              "approvalMode": "Automatic",
              "filePaths": [
                {
                  "argumentName": "path",
                  "kind": "Read",
                  "allowedRoots": ["src"]
                }
              ]
            }
            """;

        public string Schema => """{ "type": "object", "properties": {}, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubDynamicToolProvider : IDynamicToolProvider
    {
        private readonly IReadOnlyList<ITool> _tools;

        public StubDynamicToolProvider(IReadOnlyList<ITool> tools)
        {
            _tools = tools;
        }

        public IReadOnlyList<ITool> GetTools()
        {
            return _tools;
        }

        public IReadOnlyList<DynamicToolProviderStatus> GetStatuses()
        {
            return [];
        }
    }
}
