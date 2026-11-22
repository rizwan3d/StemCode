using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Infrastructure.Mcp;
using System.Net;
using System.Text;
using System.Text.Json;

namespace StemCode.Tests.Infrastructure.Mcp;

public sealed class McpServerFeaturesTests
{
    [Fact]
    public async Task InitializeAsync_Should_CaptureResourceAndPromptCapabilities()
    {
        await using McpHttpServerClient client = CreateClient(new JsonRpcRouter());

        await client.InitializeAsync(CancellationToken.None);

        client.SupportsResources().Should().BeTrue();
        client.SupportsPrompts().Should().BeTrue();
    }

    [Fact]
    public async Task ListResourcesAsync_Should_FollowPagination()
    {
        JsonRpcRouter router = new();
        router.Results["resources/list"] = request => request.Cursor switch
        {
            null => """{"resources":[{"uri":"file:///a.md","name":"A"}],"nextCursor":"p2"}""",
            "p2" => """{"resources":[{"uri":"db://rows/1","name":"Row 1","description":"First row","mimeType":"application/json"}]}""",
            _ => throw new InvalidOperationException()
        };
        await using McpHttpServerClient client = CreateClient(router);

        IReadOnlyList<McpRemoteResource> resources = await client.ListResourcesAsync(CancellationToken.None);

        resources.Select(static resource => resource.Uri).Should().Equal("file:///a.md", "db://rows/1");
        resources[1].Description.Should().Be("First row");
        resources[1].MimeType.Should().Be("application/json");
    }

    [Fact]
    public async Task ReadResourceTextAsync_Should_ReturnTextAndSummarizeBlobs()
    {
        JsonRpcRouter router = new();
        router.Results["resources/read"] = request =>
        {
            request.Params.GetProperty("uri").GetString().Should().Be("file:///a.md");
            return """
                {"contents":[
                  {"uri":"file:///a.md","mimeType":"text/markdown","text":"# Title"},
                  {"uri":"file:///logo.png","mimeType":"image/png","blob":"AAAAAAAA"}
                ]}
                """;
        };
        await using McpHttpServerClient client = CreateClient(router);

        string text = await client.ReadResourceTextAsync("file:///a.md", CancellationToken.None);

        text.Should().Contain("<resource uri=\"file:///a.md\">");
        text.Should().Contain("# Title");
        text.Should().Contain("mimeType=\"image/png\">[binary content, about 6 bytes, not shown]");
    }

    [Fact]
    public async Task Prompts_Should_ListArgumentsAndFlattenMessages()
    {
        JsonRpcRouter router = new();
        router.Results["prompts/list"] = _ => """
            {"prompts":[{"name":"review","description":"Review code","arguments":[
              {"name":"file","required":true},{"name":"focus","description":"Area to focus on"}]}]}
            """;
        router.Results["prompts/get"] = request =>
        {
            request.Params.GetProperty("name").GetString().Should().Be("review");
            request.Params.GetProperty("arguments").GetProperty("file").GetString().Should().Be("src/app.cs");
            return """
                {"messages":[
                  {"role":"user","content":{"type":"text","text":"Review src/app.cs"}},
                  {"role":"assistant","content":{"type":"text","text":"Sure."}},
                  {"role":"user","content":{"type":"resource","resource":{"uri":"file:///src/app.cs","text":"class App {}"}}}
                ]}
                """;
        };
        await using McpHttpServerClient client = CreateClient(router);

        IReadOnlyList<McpRemotePrompt> prompts = await client.ListPromptsAsync(CancellationToken.None);
        string text = await client.GetPromptTextAsync(
            "review",
            new Dictionary<string, string> { ["file"] = "src/app.cs" },
            CancellationToken.None);

        prompts.Should().ContainSingle();
        prompts[0].Arguments.Select(static argument => (argument.Name, argument.Required))
            .Should()
            .Equal(("file", true), ("focus", false));
        text.Should().Contain("[user]\nReview src/app.cs".Replace("\n", Environment.NewLine));
        text.Should().Contain("[assistant]");
        text.Should().Contain("class App {}");
    }

    [Fact]
    public void FlattenPromptMessages_Should_ReturnPlainText_ForSingleUserMessage()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"messages":[{"role":"user","content":{"type":"text","text":"Just do it."}}]}""");

        McpServerFeatures.FlattenPromptMessages(document.RootElement).Should().Be("Just do it.");
    }

    [Fact]
    public async Task ResourceTools_Should_ListAndReadThroughServer()
    {
        JsonRpcRouter router = new();
        router.Results["resources/list"] = _ => """{"resources":[{"uri":"file:///a.md","name":"A"}]}""";
        router.Results["resources/templates/list"] = _ => """{"resourceTemplates":[{"uriTemplate":"db://rows/{id}","name":"Row"}]}""";
        router.Results["resources/read"] = _ => """{"contents":[{"uri":"file:///a.md","text":"hello"}]}""";
        await using McpHttpServerClient client = CreateClient(router);
        Dictionary<string, IMcpServerClient> clients = new() { ["docs"] = client };

        McpListResourcesTool list = new(clients);
        McpReadResourceTool read = new(clients, TimeSpan.FromSeconds(5));

        ToolResult listResult = await list.ExecuteAsync(CreateContext(list.Name, "{}"), CancellationToken.None);
        ToolResult readResult = await read.ExecuteAsync(
            CreateContext(read.Name, """{"server":"DOCS","uri":"file:///a.md"}"""),
            CancellationToken.None);
        ToolResult unknownResult = await read.ExecuteAsync(
            CreateContext(read.Name, """{"server":"other","uri":"x"}"""),
            CancellationToken.None);

        listResult.Status.Should().Be(ToolResultStatus.Success);
        listResult.RenderPayload!.Text.Should().Contain("file:///a.md").And.Contain("db://rows/{id}");
        readResult.Status.Should().Be(ToolResultStatus.Success);
        JsonDocument.Parse(readResult.JsonResult).RootElement.GetProperty("content").GetString()
            .Should().Contain("hello");
        unknownResult.Status.Should().Be(ToolResultStatus.InvalidArguments);
        unknownResult.JsonResult.Should().Contain("unknown_mcp_server");
    }

    private static ToolExecutionContext CreateContext(string toolName, string argumentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(argumentsJson);
        return new ToolExecutionContext(
            "call_1",
            toolName,
            document.RootElement.Clone(),
            new ReplSessionContext(
                new StemCode.Domain.Models.AgentProviderProfile(StemCode.Domain.Models.ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
                "model",
                ["model"]));
    }

    private static McpHttpServerClient CreateClient(JsonRpcRouter router)
    {
        return new McpHttpServerClient(
            new HttpClient(router),
            new McpServerConfiguration("docs") { Url = "https://mcp.example.com/mcp" },
            TimeSpan.FromSeconds(5));
    }

    internal sealed record JsonRpcRequest(string Method, JsonElement Params)
    {
        public string? Cursor => Params.ValueKind == JsonValueKind.Object &&
                                 Params.TryGetProperty("cursor", out JsonElement cursor)
            ? cursor.GetString()
            : null;
    }

    /// <summary>Answers JSON-RPC requests over HTTP from a per-method result table.</summary>
    internal sealed class JsonRpcRouter : HttpMessageHandler
    {
        public Dictionary<string, Func<JsonRpcRequest, string>> Results { get; } = new()
        {
            ["initialize"] = _ => """
                {"protocolVersion":"2025-06-18","capabilities":{"tools":{},"resources":{"listChanged":true},"prompts":{}},"serverInfo":{"name":"fake","version":"1"}}
                """
        };

        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            HttpResponseMessage? intercepted = Intercept?.Invoke(request);
            if (intercepted is not null)
            {
                return intercepted;
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string method = root.GetProperty("method").GetString()!;
            if (!root.TryGetProperty("id", out JsonElement id))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p.Clone() : default;
            string json = Results.TryGetValue(method, out Func<JsonRpcRequest, string>? handler)
                ? $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{handler(new JsonRpcRequest(method, parameters))}}}"
                : $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"error\":{{\"code\":-32601,\"message\":\"Method not found\"}}}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}

public sealed class McpDynamicToolProviderFeatureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("stemcode-mcp-provider").FullName;

    [Fact]
    public async Task Provider_Should_ExposeResourceToolsAndPromptCommands_ForCapableServers()
    {
        McpServerFeaturesTests.JsonRpcRouter router = new();
        router.Results["tools/list"] = _ => """{"tools":[]}""";
        router.Results["prompts/list"] = _ => """{"prompts":[{"name":"review","description":"Review a file","arguments":[{"name":"file","required":true}]}]}""";
        router.Results["prompts/get"] = request =>
            "{\"messages\":[{\"role\":\"user\",\"content\":{\"type\":\"text\",\"text\":\"Please review " + request.Params.GetProperty("arguments").GetProperty("file").GetString() + "\"}}]}";

        StemCode.Application.Backend.BackendMcpServerConfiguration server = new("docs") { Url = "https://mcp.example.com/mcp" };
        server.Mark(nameof(server.Url));
        StemCodeMcpConfigLoader loader = new(
            new StemCode.Sdk.Internal.FixedWorkspaceRootProvider(_root),
            new TempUserDataPathProvider(_root),
            [server]);
        await using McpDynamicToolProvider sut = new(
            loader,
            new SingleClientFactory(router),
            new StemCode.Application.Models.ToolExecutionSettings(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<McpDynamicToolProvider>.Instance);

        sut.GetStatuses().Should().OnlyContain(static status => status.IsAvailable, string.Join(" | ", sut.GetStatuses().Select(static status => status.Details)));
        sut.GetTools().Select(static tool => tool.Name)
            .Should()
            .BeEquivalentTo([McpListResourcesTool.ToolName, McpReadResourceTool.ToolName]);
        sut.GetStatuses().Should().ContainSingle()
            .Which.Details.Should().Contain("resources").And.Contain("1 prompt(s)");

        IReadOnlyList<McpPromptDescriptor> prompts = sut.ListPrompts();
        prompts.Should().ContainSingle();
        prompts[0].CommandName.Should().Be("mcp__docs__review");
        prompts[0].Usage.Should().Be("/mcp__docs__review <file>");

        McpPromptExpansion? expanded = await sut.TryExpandAsync("/mcp__docs__review src/app.cs", CancellationToken.None);
        expanded!.Prompt.Should().Be("Please review src/app.cs");

        McpPromptExpansion? unknown = await sut.TryExpandAsync("/mcp__docs__nope", CancellationToken.None);
        unknown!.Prompt.Should().BeNull();
        unknown.Error.Should().Contain("/mcp__docs__review");

        (await sut.TryExpandAsync("plain prompt", CancellationToken.None)).Should().BeNull();
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class TempUserDataPathProvider(string root) : IUserDataPathProvider
    {
        public string GetConfigurationFilePath() => Path.Combine(root, "user", "agent-profile.json");

        public string GetMcpConfigurationFilePath() => Path.Combine(root, "user", "mcp.toml");

        public string GetLogsDirectoryPath() => Path.Combine(root, "user", "logs");

        public string GetSessionsDirectoryPath() => Path.Combine(root, "user", "sessions");
    }
}
