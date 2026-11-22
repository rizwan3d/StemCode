using FluentAssertions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Application.UI;
using StemCode.CLI;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace StemCode.Tests.CLI;

public sealed class AcpServerTests
{
    [Fact]
    public async Task RunAsync_Should_HandleInitializeSessionAndPrompt()
    {
        string cwd = Directory.GetCurrentDirectory();
        FakeBackend backend = new();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                ",\"mcpServers\":[]}}",
            """
            {"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"sess-test","prompt":[{"type":"text","text":"Hello"},{"type":"resource","resource":{"uri":"file:///notes.txt","mimeType":"text/plain","text":"context"}}]}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());

        JsonElement initialize = FindResponse(messages, 1);
        initialize.GetProperty("result")
            .GetProperty("agentInfo")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("stemcode");

        JsonElement sessionNew = FindResponse(messages, 2);
        sessionNew.GetProperty("result")
            .GetProperty("sessionId")
            .GetString()
            .Should()
            .Be("sess-test");

        messages.Should().Contain(message =>
            IsSessionUpdate(message, "session_info_update") &&
            message.GetProperty("params").GetProperty("update").GetProperty("modelId").GetString() == "gpt-test");
        messages.Should().Contain(message =>
            IsSessionUpdate(message, "session_info_update") &&
            message.GetProperty("params")
                .GetProperty("update")
                .GetProperty("availableAgentProfiles")
                .EnumerateArray()
                .Any(profile => profile.GetProperty("name").GetString() == "custom-review"));
        messages.Should().Contain(message => IsSessionUpdate(message, "plan"));
        messages.Should().Contain(message => IsSessionUpdate(message, "tool_call"));
        messages.Should().Contain(message => IsSessionUpdate(message, "tool_call_update"));
        JsonElement toolUpdate = messages.Single(message => IsSessionUpdate(message, "tool_call_update"));
        toolUpdate
            .GetProperty("params")
            .GetProperty("update")
            .GetProperty("content")[0]
            .GetProperty("content")
            .GetProperty("text")
            .GetString()
            .Should()
            .Contain("\u2022 Ran dotnet test");
        messages.Should().Contain(message =>
            IsSessionUpdate(message, "agent_reasoning_chunk") &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() ==
                "Inspect first.");
        messages.Should().Contain(message =>
            IsSessionUpdate(message, "agent_message_chunk") &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() == "Done.");

        JsonElement prompt = FindResponse(messages, 3);
        prompt.GetProperty("result")
            .GetProperty("stopReason")
            .GetString()
            .Should()
            .Be("end_turn");

        backend.LastInput.Should().Contain("Hello");
        backend.LastInput.Should().Contain("Resource: file:///notes.txt");
    }

    [Fact]
    public async Task RunAsync_Should_AdvertiseAvailableCommands_AfterSessionNew()
    {
        string cwd = Directory.GetCurrentDirectory();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(reader, output, error, backendArgs: [], providerAuthKey: null, _ => new FakeBackend());

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        JsonElement update = messages
            .Single(message => IsSessionUpdate(message, "available_commands_update"))
            .GetProperty("params")
            .GetProperty("update");
        JsonElement[] commands = update.GetProperty("availableCommands").EnumerateArray().ToArray();
        commands.Select(static command => command.GetProperty("name").GetString())
            .Should()
            .Contain(["compact", "doctor", "fork", "undo", "use"]);
        JsonElement compact = commands.Single(static command => command.GetProperty("name").GetString() == "compact");
        compact.GetProperty("usage").GetString().Should().Be("/compact [retained-turns]");
        compact.GetProperty("input").GetProperty("hint").GetString().Should().Be("[retained-turns]");
        commands.Single(static command => command.GetProperty("name").GetString() == "doctor")
            .TryGetProperty("input", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task RunAsync_Should_ResetAndReplayHistory_WhenCommandSwitchesSession()
    {
        string cwd = Directory.GetCurrentDirectory();
        FakeBackend backend = new() { SwitchSessionOnCommand = "sess-forked" };
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}",
            """
            {"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"sess-test","prompt":[{"type":"text","text":"/fork 1"}]}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(reader, output, error, backendArgs: [], providerAuthKey: null, _ => backend);

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        backend.LastCommand.Should().Be("/fork 1");

        int resetIndex = IndexOf(messages, message => IsSessionUpdate(message, "session_history_reset"));
        resetIndex.Should().BeGreaterThanOrEqualTo(0);
        messages[resetIndex].GetProperty("params").GetProperty("sessionId").GetString().Should().Be("sess-test");

        // History is replayed after the reset, under the client-facing ACP session id.
        int userIndex = IndexOf(messages, message =>
            IsSessionUpdate(message, "user_message_chunk") &&
            message.GetProperty("params").GetProperty("sessionId").GetString() == "sess-test" &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() ==
                "earlier question");
        int agentIndex = IndexOf(messages, message =>
            IsSessionUpdate(message, "agent_message_chunk") &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() ==
                "earlier answer");
        int confirmationIndex = IndexOf(messages, message =>
            IsSessionUpdate(message, "agent_message_chunk") &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() ==
                "Forked.");

        userIndex.Should().BeGreaterThan(resetIndex);
        agentIndex.Should().BeGreaterThan(userIndex);
        confirmationIndex.Should().BeGreaterThan(agentIndex);
        FindResponse(messages, 3).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");
    }

    private static int IndexOf(IReadOnlyList<JsonElement> messages, Func<JsonElement, bool> predicate)
    {
        for (int index = 0; index < messages.Count; index++)
        {
            if (predicate(messages[index]))
            {
                return index;
            }
        }

        return -1;
    }

    [Fact]
    public async Task RunAsync_Should_AdvertiseAndRequireAuthentication_WhenAcpTokenConfigured()
    {
        string cwd = Directory.GetCurrentDirectory();
        bool backendCreated = false;
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) =>
            {
                backendCreated = true;
                return new FakeBackend();
            },
            acpAuthenticationToken: "acp-secret");

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        JsonElement initialize = FindResponse(messages, 1);
        initialize.GetProperty("result")
            .GetProperty("authMethods")
            .EnumerateArray()
            .Select(static method => method.GetString())
            .Should()
            .Equal("token");

        JsonElement sessionNew = FindResponse(messages, 2);
        sessionNew.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32001);
        sessionNew.GetProperty("error").GetProperty("message").GetString().Should().Contain("authentication is required");
        backendCreated.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Should_RejectAuthenticate_WhenTokenDoesNotMatch()
    {
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            """
            {"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"token":"wrong-token"}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) => new FakeBackend(),
            acpAuthenticationToken: "acp-secret");

        await sut.RunAsync(CancellationToken.None);

        JsonElement authenticate = FindResponse(ParseJsonLines(output.ToString()), 2);
        authenticate.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32001);
        authenticate.GetProperty("error").GetProperty("message").GetString().Should().Be("Invalid ACP authentication token.");
    }

    [Fact]
    public async Task RunAsync_Should_AllowSessionMethods_AfterSuccessfulAuthentication()
    {
        string cwd = Directory.GetCurrentDirectory();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            """
            {"jsonrpc":"2.0","id":2,"method":"authenticate","params":{"token":"acp-secret"}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) => new FakeBackend(),
            acpAuthenticationToken: "acp-secret");

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        FindResponse(messages, 2).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
        FindResponse(messages, 3).GetProperty("result").GetProperty("sessionId").GetString().Should().Be("sess-test");
    }

    [Fact]
    public async Task RunAsync_Should_AcceptStringAuthenticateParams()
    {
        string cwd = Directory.GetCurrentDirectory();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            """
            {"jsonrpc":"2.0","id":2,"method":"authenticate","params":"acp-secret"}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) => new FakeBackend(),
            acpAuthenticationToken: "acp-secret");

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        FindResponse(messages, 2).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
        FindResponse(messages, 3).GetProperty("result").GetProperty("sessionId").GetString().Should().Be("sess-test");
    }

    [Fact]
    public async Task RunAsync_Should_RejectOversizedIncomingLine_AndContinueProcessing()
    {
        string oversizedLine = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + new string('a', 120) + "\"}";
        string input = oversizedLine + Environment.NewLine +
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":1}}""";

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) => new FakeBackend(),
            maxIncomingLineLength: 96);

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        messages.Should().Contain(message =>
            message.GetProperty("error").GetProperty("code").GetInt32() == -32600 &&
            message.GetProperty("error").GetProperty("message").GetString()!.Contains("maximum size of 96 characters"));
        FindResponse(messages, 2).GetProperty("result").GetProperty("protocolVersion").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Should_RejectRequestsThatExceedRateLimit()
    {
        string input = string.Join(
            Environment.NewLine,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""",
            """{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":1}}""");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            backendFactory: (_, _) => new FakeBackend(),
            maxRequestsPerWindow: 1,
            requestRateLimitWindow: TimeSpan.FromMinutes(1));

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        FindResponse(messages, 1).GetProperty("result").GetProperty("protocolVersion").GetInt32().Should().Be(1);
        FindResponse(messages, 2).GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32002);
        FindResponse(messages, 2).GetProperty("error").GetProperty("message").GetString().Should().Be("ACP request rate limit exceeded.");
    }

    [Fact]
    public async Task RunAsync_Should_ScopeInitializeAndSessionMcpServersToBackend()
    {
        string cwd = Directory.GetCurrentDirectory();
        FakeBackend backend = new();
        IReadOnlyList<BackendMcpServerConfiguration>? capturedMcpServers = null;
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"mcpServers":[{"name":"init-server","command":"node","args":["init.js"],"env":[{"name":"INIT_TOKEN","value":"secret"}]}]}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                """
                ,"mcpServers":[{"type":"http","name":"editor-server","url":"http://127.0.0.1:9876/mcp","headers":[{"name":"X-Editor","value":"stemcode"}]}]}}
                """,
            """
            {"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"sess-test","prompt":[{"type":"text","text":"/mcp"}]}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: ["--profile", "review"],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            (args, mcpServers) =>
            {
                args.Should().Contain("--profile");
                capturedMcpServers = mcpServers;
                return backend;
            });

        await sut.RunAsync(CancellationToken.None);

        capturedMcpServers.Should().NotBeNull();
        capturedMcpServers!.Select(static server => server.Name)
            .Should()
            .Equal("init-server", "editor-server");

        BackendMcpServerConfiguration initServer = capturedMcpServers[0];
        initServer.Command.Should().Be("node");
        initServer.Args.Should().Equal("init.js");
        initServer.Env.Should().Contain("INIT_TOKEN", "secret");
        initServer.Source.Should().Be("ACP initialize");

        BackendMcpServerConfiguration editorServer = capturedMcpServers[1];
        editorServer.Url.Should().Be("http://127.0.0.1:9876/mcp");
        editorServer.Command.Should().BeNull();
        editorServer.IsAssigned(nameof(BackendMcpServerConfiguration.Command)).Should().BeTrue();
        editorServer.HttpHeaders.Should().Contain("X-Editor", "stemcode");
        editorServer.Source.Should().Be("ACP session");
        backend.LastCommand.Should().Be("/mcp");
        ParseJsonLines(output.ToString()).Should().Contain(message =>
            IsSessionUpdate(message, "agent_message_chunk") &&
            message.GetProperty("params").GetProperty("update").GetProperty("content").GetProperty("text").GetString() == "command:/mcp");
    }

    [Fact]
    public async Task RunAsync_Should_OnlyForwardNoUpdateCheck_WhenRequested()
    {
        string cwd = Directory.GetCurrentDirectory();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        string[]? argsWithoutSkip = null;
        using (StringReader reader = new(input))
        using (StringWriter output = new())
        using (StringWriter error = new())
        {
            AcpServer sut = new(
                reader,
                output,
                error,
                backendArgs: ["--profile", "review"],
                providerAuthKey: null,
                replayLoadedSessionHistory: true,
                autoApproveAllTools: false,
                (args, _) =>
                {
                    argsWithoutSkip = args;
                    return new FakeBackend();
                });

            await sut.RunAsync(CancellationToken.None);
        }

        argsWithoutSkip.Should().NotBeNull();
        argsWithoutSkip!.Should().Equal("--profile", "review");

        string[]? argsWithSkip = null;
        using (StringReader reader = new(input))
        using (StringWriter output = new())
        using (StringWriter error = new())
        {
            AcpServer sut = new(
                reader,
                output,
                error,
                backendArgs: ["--profile", "review", "--no-update-check"],
                providerAuthKey: null,
                replayLoadedSessionHistory: true,
                autoApproveAllTools: false,
                (args, _) =>
                {
                    argsWithSkip = args;
                    return new FakeBackend();
                });

            await sut.RunAsync(CancellationToken.None);
        }

        argsWithSkip.Should().NotBeNull();
        argsWithSkip!.Should().Equal("--profile", "review", "--no-update-check");
    }

    [Fact]
    public async Task RunAsync_Should_AllowMultipleActiveSessions()
    {
        string cwd = Directory.GetCurrentDirectory();
        List<FakeBackend> backends = [];
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}",
            """
            {"jsonrpc":"2.0","id":4,"method":"session/prompt","params":{"sessionId":"sess-1","prompt":[{"type":"text","text":"First"}]}}
            """,
            """
            {"jsonrpc":"2.0","id":5,"method":"session/prompt","params":{"sessionId":"sess-2","prompt":[{"type":"text","text":"Second"}]}}
            """,
            """
            {"jsonrpc":"2.0","id":6,"method":"session/close","params":{"sessionId":"sess-1"}}
            """,
            """
            {"jsonrpc":"2.0","id":7,"method":"session/prompt","params":{"sessionId":"sess-2","prompt":[{"type":"text","text":"Still active"}]}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ =>
            {
                FakeBackend backend = new($"sess-{backends.Count + 1}");
                backends.Add(backend);
                return backend;
            });

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        FindResponse(messages, 2).GetProperty("result").GetProperty("sessionId").GetString().Should().Be("sess-1");
        FindResponse(messages, 3).GetProperty("result").GetProperty("sessionId").GetString().Should().Be("sess-2");
        FindResponse(messages, 4).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");
        FindResponse(messages, 5).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");
        FindResponse(messages, 6).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Object);
        FindResponse(messages, 7).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");

        backends.Should().HaveCount(2);
        backends[0].Inputs.Should().Equal("First");
        backends[1].Inputs.Should().Equal("Second", "Still active");
        backends[0].DisposeCount.Should().Be(1);
        backends[1].DisposeCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_Should_OverlapPromptsAcrossSessions()
    {
        string cwd = Directory.GetCurrentDirectory();
        ConcurrentPromptProbe probe = new();
        List<ConcurrentPromptBackend> backends = [];
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}",
            """
            {"jsonrpc":"2.0","id":4,"method":"session/prompt","params":{"sessionId":"sess-1","prompt":[{"type":"text","text":"First"}]}}
            """,
            """
            {"jsonrpc":"2.0","id":5,"method":"session/prompt","params":{"sessionId":"sess-2","prompt":[{"type":"text","text":"Second"}]}}
            """);

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ =>
            {
                ConcurrentPromptBackend backend = new($"sess-{backends.Count + 1}", probe);
                backends.Add(backend);
                return backend;
            });

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        FindResponse(messages, 4).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");
        FindResponse(messages, 5).GetProperty("result").GetProperty("stopReason").GetString().Should().Be("end_turn");
        probe.MaxConcurrentTurns.Should().BeGreaterThan(1);
        backends[0].Inputs.Should().Equal("First");
        backends[1].Inputs.Should().Equal("Second");
    }

    [Fact]
    public async Task RunAsync_Should_HandleOnboardingPromptsBeforeSessionExists()
    {
        string cwd = Directory.GetCurrentDirectory();
        PromptingBackend backend = new();
        ScriptedAcpTransport transport = new();
        using StringWriter error = new();
        AcpServer sut = new(
            transport.Input,
            transport.Output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        Task runTask = sut.RunAsync(CancellationToken.None);

        await transport.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        await transport.SendAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
            JsonSerializer.Serialize(cwd) +
            "}}");
        await transport.WaitForSessionNewAsync();
        transport.CompleteInput();
        await runTask;

        IReadOnlyList<JsonElement> messages = transport.Messages;

        messages.Should().Contain(message =>
            IsMethod(message, "session/request_permission") &&
            message.GetProperty("params").GetProperty("toolCall").GetProperty("title").GetString() == "Choose provider");
        messages.Should().Contain(message =>
            IsMethod(message, "session/request_text") &&
            message.GetProperty("params").GetProperty("label").GetString() == "API key" &&
            message.GetProperty("params").GetProperty("isSecret").GetBoolean());
        IndexOfMessage(messages, message =>
                IsMethod(message, "session/request_permission") &&
                message.GetProperty("params").GetProperty("toolCall").GetProperty("title").GetString() == "Choose provider")
            .Should()
            .BeLessThan(IndexOfMessage(messages, message =>
                IsMethod(message, "session/request_text") &&
                message.GetProperty("params").GetProperty("label").GetString() == "API key"));

        FindResponse(messages, 2).GetProperty("result").GetProperty("sessionId").GetString().Should().Be("sess-prompt");
        backend.SelectedProvider.Should().Be("OpenAI");
        backend.ApiKey.Should().Be("sk-test");
    }

    [Fact]
    public async Task RunAsync_Should_IncludePermissionDefaultAndTimeoutMetadata()
    {
        string cwd = Directory.GetCurrentDirectory();
        PermissionMetadataBackend backend = new();
        ScriptedAcpTransport transport = new();
        using StringWriter error = new();
        AcpServer sut = new(
            transport.Input,
            transport.Output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        Task runTask = sut.RunAsync(CancellationToken.None);

        await transport.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        await transport.SendAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
            JsonSerializer.Serialize(cwd) +
            "}}");
        await transport.WaitForSessionNewAsync();
        transport.CompleteInput();
        await runTask;

        JsonElement permissionRequest = transport.Messages.Single(message =>
            IsMethod(message, "session/request_permission") &&
            message.GetProperty("params").GetProperty("toolCall").GetProperty("title").GetString() == "Approve shell command?");
        JsonElement parameters = permissionRequest.GetProperty("params");
        parameters.GetProperty("allowCancellation").GetBoolean().Should().BeTrue();
        parameters.GetProperty("defaultOptionId").GetString().Should().Be("0");
        parameters.GetProperty("autoSelectAfterMilliseconds").GetInt32().Should().Be(10_000);

        JsonElement content = parameters
            .GetProperty("toolCall")
            .GetProperty("content")[0];
        content.GetProperty("type").GetString().Should().Be("content");
        content.GetProperty("content").GetProperty("type").GetString().Should().Be("text");
        content.GetProperty("content").GetProperty("text").GetString().Should().Contain("Tool: shell_command");
        backend.SelectedChoice.Should().Be("allow");
    }

    [Fact]
    public async Task RunAsync_Should_ApplyConfiguredAcpRequestTimeoutFromAgentProfile()
    {
        string originalDirectory = Directory.GetCurrentDirectory();
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".stemcode"));
        File.WriteAllText(
            Path.Combine(workspace.Path, ".stemcode", "agent-profile.json"),
            """
            {
              "Application": {
                "Tools": {
                  "acpRequestTimeoutSeconds": 1
                }
              }
            }
            """);

        PromptingBackend backend = new();
        NonResponsiveAcpTransport transport = new();
        using StringWriter error = new();
        AcpServer sut = new(
            transport.Input,
            transport.Output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        Task runTask = sut.RunAsync(CancellationToken.None);

        try
        {
            await transport.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
            await transport.SendAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
                JsonSerializer.Serialize(workspace.Path) +
                "}}");
            await transport.WaitForMethodAsync("session/request_permission");
            transport.CompleteInput();
            await runTask;

            JsonElement response = FindResponse(transport.Messages, 2);
            response.GetProperty("error")
                .GetProperty("message")
                .GetString()
                .Should()
                .Contain("ACP request 'session/request_permission' timed out after 1 seconds.");
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task RunAsync_Should_HandleOpenAiCompatibleOnboardingPromptSequenceBeforeSessionExists()
    {
        string cwd = Directory.GetCurrentDirectory();
        CompatiblePromptingBackend backend = new();
        ScriptedAcpTransport transport = new();
        using StringWriter error = new();
        AcpServer sut = new(
            transport.Input,
            transport.Output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        Task runTask = sut.RunAsync(CancellationToken.None);

        await transport.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}""");
        await transport.SendAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/new\",\"params\":{\"cwd\":" +
            JsonSerializer.Serialize(cwd) +
            "}}");
        await transport.WaitForSessionNewAsync();
        transport.CompleteInput();
        await runTask;

        IReadOnlyList<JsonElement> messages = transport.Messages;
        int providerIndex = IndexOfMessage(messages, message =>
            IsMethod(message, "session/request_permission") &&
            message.GetProperty("params").GetProperty("toolCall").GetProperty("title").GetString() == "Choose provider");
        int baseUrlIndex = IndexOfMessage(messages, message =>
            IsMethod(message, "session/request_text") &&
            message.GetProperty("params").GetProperty("label").GetString() == "Base URL");
        int apiKeyIndex = IndexOfMessage(messages, message =>
            IsMethod(message, "session/request_text") &&
            message.GetProperty("params").GetProperty("label").GetString() == "API key");

        providerIndex.Should().BeLessThan(baseUrlIndex);
        baseUrlIndex.Should().BeLessThan(apiKeyIndex);
        backend.SelectedProvider.Should().Be("OpenAI-compatible provider");
        backend.BaseUrl.Should().Be("https://compatible.example.com/v1");
        backend.ApiKey.Should().Be("sk-test");
    }

    [Fact]
    public void Parse_Should_Not_ReadRedirectedStdin_WhenAcpModeIsSelected()
    {
        bool readCalled = false;

        CliInvocation invocation = CliInvocation.Parse(
            ["--acp", "--profile", "review"],
            stdinRedirected: true,
            () =>
            {
                readCalled = true;
                return "protocol input";
            });

        invocation.Mode.Should().Be(CliMode.Acp);
        invocation.BackendArgs.Should().Equal("--profile", "review");
        readCalled.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Should_ReplayAssistantReasoningHistoryAsThinkingChunk()
    {
        string cwd = Directory.GetCurrentDirectory();
        string sectionId = Guid.NewGuid().ToString("D");
        HistoryBackend backend = new();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/load\",\"params\":{\"sessionId\":\"" +
                sectionId +
                "\",\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ => backend);

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        int thinkingIndex = IndexOfMessage(messages, message =>
            IsSessionUpdate(message, "agent_reasoning_chunk") &&
            message.GetProperty("params")
                .GetProperty("update")
                .GetProperty("content")
                .GetProperty("type")
                .GetString() == "thinking" &&
            message.GetProperty("params")
                .GetProperty("update")
                .GetProperty("content")
                .GetProperty("text")
                .GetString() == "Inspect first.");
        int assistantIndex = IndexOfMessage(messages, message =>
            IsSessionUpdate(message, "agent_message_chunk") &&
            message.GetProperty("params")
                .GetProperty("update")
                .GetProperty("content")
                .GetProperty("text")
                .GetString() == "I updated it.");

        thinkingIndex.Should().BeLessThan(assistantIndex);
    }

    [Fact]
    public async Task RunAsync_Should_SkipHistoryReplay_WhenNoOldReaderIsEnabled()
    {
        string cwd = Directory.GetCurrentDirectory();
        string sectionId = Guid.NewGuid().ToString("D");
        HistoryBackend backend = new();
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/load\",\"params\":{\"sessionId\":\"" +
                sectionId +
                "\",\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: false,
            autoApproveAllTools: false,
            (_, _) => backend);

        await sut.RunAsync(CancellationToken.None);

        IReadOnlyList<JsonElement> messages = ParseJsonLines(output.ToString());
        messages.Should().NotContain(message =>
            IsSessionUpdate(message, "agent_reasoning_chunk") ||
            IsSessionUpdate(message, "agent_message_chunk") ||
            IsSessionUpdate(message, "user_message_chunk"));
        FindResponse(messages, 2).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RunAsync_Should_RejectSessionLoad_WhenSessionIdIsNotGuid()
    {
        string cwd = Directory.GetCurrentDirectory();
        bool backendCreated = false;
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/load\",\"params\":{\"sessionId\":\"../other-session\",\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            _ =>
            {
                backendCreated = true;
                return new FakeBackend();
            });

        await sut.RunAsync(CancellationToken.None);

        JsonElement response = FindResponse(ParseJsonLines(output.ToString()), 2);
        response.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32602);
        response.GetProperty("error").GetProperty("message").GetString().Should().Be("sessionId must be a valid GUID.");
        backendCreated.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Should_NormalizeGuidSessionId_ForSessionLoad()
    {
        string cwd = Directory.GetCurrentDirectory();
        string normalizedSessionId = Guid.NewGuid().ToString("D");
        string suppliedSessionId = "{" + normalizedSessionId.ToUpperInvariant() + "}";
        string[]? capturedArgs = null;
        string input = string.Join(
            Environment.NewLine,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1}}
            """,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"session/load\",\"params\":{\"sessionId\":\"" +
                suppliedSessionId +
                "\",\"cwd\":" +
                JsonSerializer.Serialize(cwd) +
                "}}");

        using StringReader reader = new(input);
        using StringWriter output = new();
        using StringWriter error = new();
        AcpServer sut = new(
            reader,
            output,
            error,
            backendArgs: [],
            providerAuthKey: null,
            replayLoadedSessionHistory: true,
            autoApproveAllTools: false,
            (args, _) =>
            {
                capturedArgs = args;
                return new FakeBackend(normalizedSessionId);
            });

        await sut.RunAsync(CancellationToken.None);

        capturedArgs.Should().NotBeNull();
        capturedArgs.Should().ContainInOrder("--section", normalizedSessionId);
        FindResponse(ParseJsonLines(output.ToString()), 2).GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static JsonElement FindResponse(IReadOnlyList<JsonElement> messages, int id)
    {
        return messages.Single(message =>
            message.TryGetProperty("id", out JsonElement responseId) &&
            responseId.ValueKind == JsonValueKind.Number &&
            responseId.GetInt32() == id &&
            !message.TryGetProperty("method", out _) &&
            (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)));
    }

    private static bool IsSessionUpdate(JsonElement message, string updateKind)
    {
        return message.TryGetProperty("method", out JsonElement method) &&
            method.GetString() == "session/update" &&
            message.TryGetProperty("params", out JsonElement parameters) &&
            parameters.TryGetProperty("update", out JsonElement update) &&
            update.TryGetProperty("sessionUpdate", out JsonElement sessionUpdate) &&
            sessionUpdate.GetString() == updateKind;
    }

    private static bool IsMethod(JsonElement message, string methodName)
    {
        return message.TryGetProperty("method", out JsonElement method) &&
            method.GetString() == methodName;
    }

    private static int IndexOfMessage(
        IReadOnlyList<JsonElement> messages,
        Func<JsonElement, bool> predicate)
    {
        for (int index = 0; index < messages.Count; index++)
        {
            if (predicate(messages[index]))
            {
                return index;
            }
        }

        throw new InvalidOperationException("Expected ACP message was not found.");
    }

    private static IReadOnlyList<JsonElement> ParseJsonLines(string output)
    {
        List<JsonElement> messages = [];
        foreach (string line in output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            using JsonDocument document = JsonDocument.Parse(line);
            messages.Add(document.RootElement.Clone());
        }

        return messages;
    }

    private sealed class FakeBackend : IStemCodeBackend
    {
        private readonly string _sessionId;

        public FakeBackend(string sessionId = "sess-test")
        {
            _sessionId = sessionId;
        }

        public int DisposeCount { get; private set; }

        public List<string> Inputs { get; } = [];

        public string LastCommand { get; private set; } = string.Empty;

        public string LastInput { get; private set; } = string.Empty;

        /// <summary>When set, commands switch the backend to this section id, like /fork.</summary>
        public string? SwitchSessionOnCommand { get; init; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateSessionInfo());
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            LastCommand = commandText;
            if (SwitchSessionOnCommand is not null)
            {
                BackendSessionInfo switched = CreateSessionInfo() with
                {
                    SessionId = SwitchSessionOnCommand,
                    ConversationHistory =
                    [
                        new BackendConversationMessage("user", "earlier question"),
                        new BackendConversationMessage("assistant", "earlier answer")
                    ]
                };
                return Task.FromResult(new BackendCommandResult(
                    new ReplCommandResult(ExitRequested: false, "Forked.", ReplFeedbackKind.Info, ReplaySession: true),
                    switched));
            }

            return Task.FromResult(new BackendCommandResult(
                ReplCommandResult.Continue("command:" + commandText),
                CreateSessionInfo()));
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            return RunTurnAsync(input, [], uiBridge, cancellationToken);
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            LastInput = input;
            Inputs.Add(input);
            uiBridge.ShowAssistantReasoning("Inspect first.");
            uiBridge.ShowExecutionPlan(new ExecutionPlanProgress(["Inspect context"], 0));

            ConversationToolCall toolCall = new("call-1", "shell_command", """{"command":"dotnet test"}""");
            uiBridge.ShowToolCalls([toolCall]);
            uiBridge.ShowToolResults(new ToolExecutionBatchResult(
                [
                    new ToolInvocationResult(
                        "call-1",
                        "shell_command",
                        ToolResult.Success(
                            "Command complete.",
                            """
                            {
                              "Command": "dotnet test",
                              "WorkingDirectory": ".",
                              "ExitCode": 0,
                              "StandardOutput": "Passed!\n",
                              "StandardError": ""
                            }
                            """))
                ]));

            return Task.FromResult(ConversationTurnResult.AssistantMessage("Done."));
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        private BackendSessionInfo CreateSessionInfo()
        {
            return new BackendSessionInfo(
                SessionId: _sessionId,
                SectionResumeCommand: $"stemcode --session {_sessionId}",
                ProviderName: "OpenAI",
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "off",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "build",
                SectionTitle: "Untitled section",
                IsResumedSection: false,
                ConversationHistory: [])
            {
                AvailableAgentProfiles =
                [
                    new BackendAgentProfileInfo("build", "primary", "Default build profile"),
                    new BackendAgentProfileInfo("custom-review", "primary", "Workspace custom review profile")
                ]
            };
        }
    }

    private sealed class ConcurrentPromptBackend : IStemCodeBackend
    {
        private readonly ConcurrentPromptProbe _probe;
        private readonly string _sessionId;

        public ConcurrentPromptBackend(string sessionId, ConcurrentPromptProbe probe)
        {
            _sessionId = sessionId;
            _probe = probe;
        }

        public List<string> Inputs { get; } = [];

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new BackendSessionInfo(
                SessionId: _sessionId,
                SectionResumeCommand: $"stemcode --session {_sessionId}",
                ProviderName: "OpenAI",
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "off",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "build",
                SectionTitle: "Concurrent session",
                IsResumedSection: false,
                ConversationHistory: []));
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            return RunTurnAsync(input, [], uiBridge, cancellationToken);
        }

        public async Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            Inputs.Add(input);
            await _probe.HoldTurnAsync(cancellationToken);
            return ConversationTurnResult.AssistantMessage($"Done:{_sessionId}");
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ConcurrentPromptProbe
    {
        private readonly TaskCompletionSource _overlapObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeTurns;
        private int _maxConcurrentTurns;

        public int MaxConcurrentTurns => Volatile.Read(ref _maxConcurrentTurns);

        public async Task HoldTurnAsync(CancellationToken cancellationToken)
        {
            int activeTurns = Interlocked.Increment(ref _activeTurns);
            UpdateMaxConcurrentTurns(activeTurns);
            if (activeTurns > 1)
            {
                _overlapObserved.TrySetResult();
            }

            try
            {
                await Task.WhenAny(
                    _overlapObserved.Task,
                    Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken));
            }
            finally
            {
                Interlocked.Decrement(ref _activeTurns);
            }
        }

        private void UpdateMaxConcurrentTurns(int activeTurns)
        {
            while (true)
            {
                int snapshot = Volatile.Read(ref _maxConcurrentTurns);
                if (activeTurns <= snapshot)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentTurns, activeTurns, snapshot) == snapshot)
                {
                    return;
                }
            }
        }
    }

    private sealed class HistoryBackend : IStemCodeBackend
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new BackendSessionInfo(
                SessionId: "sess-history",
                SectionResumeCommand: "stemcode --session sess-history",
                ProviderName: "OpenAI",
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "on",
                ReasoningEffort: null,
                ShowThinking: true,
                AgentProfileName: "build",
                SectionTitle: "History session",
                IsResumedSection: true,
                ConversationHistory:
                [
                    new BackendConversationMessage("user", "Please update README."),
                    new BackendConversationMessage(
                        "assistant",
                        "I updated it.",
                        ReasoningContent: "Inspect first.")
                ]));
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class PromptingBackend : IStemCodeBackend
    {
        public string ApiKey { get; private set; } = string.Empty;

        public string SelectedProvider { get; private set; } = string.Empty;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public async Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            SelectedProvider = await uiBridge.RequestSelectionAsync(
                new SelectionPromptRequest<string>(
                    "Choose provider",
                    [new SelectionPromptOption<string>("OpenAI", "OpenAI")],
                    "Pick a provider."),
                cancellationToken);

            ApiKey = await uiBridge.RequestTextAsync(
                new TextPromptRequest(
                    "API key",
                    "Paste the API key."),
                isSecret: true,
                cancellationToken);

            return new BackendSessionInfo(
                SessionId: "sess-prompt",
                SectionResumeCommand: "stemcode --session sess-prompt",
                ProviderName: SelectedProvider,
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "off",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "build",
                SectionTitle: "Prompted session",
                IsResumedSection: false,
                ConversationHistory: []);
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class PermissionMetadataBackend : IStemCodeBackend
    {
        public string SelectedChoice { get; private set; } = string.Empty;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public async Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            SelectedChoice = await uiBridge.RequestSelectionAsync(
                new SelectionPromptRequest<string>(
                    "Approve shell command?",
                    [
                        new SelectionPromptOption<string>("Allow once", "allow"),
                        new SelectionPromptOption<string>("Deny once", "deny")
                    ],
                    "Tool: shell_command\nCommand: dotnet test",
                    DefaultIndex: 0,
                    AllowCancellation: true,
                    AutoSelectAfter: TimeSpan.FromSeconds(10)),
                cancellationToken);

            return new BackendSessionInfo(
                SessionId: "sess-permission",
                SectionResumeCommand: "stemcode --session sess-permission",
                ProviderName: "OpenAI",
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "off",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "build",
                SectionTitle: "Permission session",
                IsResumedSection: false,
                ConversationHistory: []);
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class CompatiblePromptingBackend : IStemCodeBackend
    {
        public string ApiKey { get; private set; } = string.Empty;

        public string BaseUrl { get; private set; } = string.Empty;

        public string SelectedProvider { get; private set; } = string.Empty;

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public async Task<BackendSessionInfo> InitializeAsync(
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            SelectedProvider = await uiBridge.RequestSelectionAsync(
                new SelectionPromptRequest<string>(
                    "Choose provider",
                    [new SelectionPromptOption<string>("OpenAI-compatible provider", "OpenAI-compatible provider")],
                    "Pick a provider."),
                cancellationToken);

            BaseUrl = await uiBridge.RequestTextAsync(
                new TextPromptRequest(
                    "Base URL",
                    "Enter the OpenAI-compatible base URL."),
                isSecret: false,
                cancellationToken);

            ApiKey = await uiBridge.RequestTextAsync(
                new TextPromptRequest(
                    "API key",
                    "Paste the API key."),
                isSecret: true,
                cancellationToken);

            return new BackendSessionInfo(
                SessionId: "sess-compatible",
                SectionResumeCommand: "stemcode --session sess-compatible",
                ProviderName: SelectedProvider,
                ModelId: "gpt-test",
                ActiveModelContextWindowTokens: null,
                AvailableModelIds: ["gpt-test"],
                ThinkingMode: "off",
                ReasoningEffort: null,
                ShowThinking: false,
                AgentProfileName: "build",
                SectionTitle: "Compatible session",
                IsResumedSection: false,
                ConversationHistory: []);
        }

        public Task<BackendCommandResult> RunCommandAsync(
            string commandText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<BackendCommandResult> SelectModelAsync(CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ConversationTurnResult> RunTurnAsync(
            string input,
            IReadOnlyList<ConversationAttachment> attachments,
            IUiBridge uiBridge,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class ScriptedAcpTransport
    {
        private readonly Channel<string?> _input = Channel.CreateUnbounded<string?>();
        private readonly TaskCompletionSource _sessionNewCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<JsonElement> _messages = [];

        public ScriptedAcpTransport()
        {
            Input = new ChannelTextReader(_input.Reader);
            Output = new ScriptedTextWriter(this);
        }

        public TextReader Input { get; }

        public IReadOnlyList<JsonElement> Messages => _messages;

        public TextWriter Output { get; }

        public void CompleteInput()
        {
            _input.Writer.TryComplete();
        }

        public ValueTask SendAsync(string line)
        {
            return _input.Writer.WriteAsync(line);
        }

        public Task WaitForSessionNewAsync()
        {
            return _sessionNewCompletion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        private void HandleOutputLine(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement message = document.RootElement.Clone();
            _messages.Add(message);

            if (message.TryGetProperty("method", out JsonElement method))
            {
                string? methodName = method.GetString();
                if (methodName == "session/request_permission")
                {
                    Respond(
                        message,
                        """{"outcome":{"outcome":"selected","optionId":"0"}}""");
                }
                else if (methodName == "session/request_text")
                {
                    string label = message
                        .GetProperty("params")
                        .GetProperty("label")
                        .GetString() ?? string.Empty;
                    string value = label == "Base URL"
                        ? "https://compatible.example.com/v1"
                        : "sk-test";
                    Respond(
                        message,
                        """{"outcome":{"outcome":"submitted","value":""" +
                        JsonSerializer.Serialize(value) +
                        "}}");
                }
            }

            if (message.TryGetProperty("id", out JsonElement id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.GetInt32() == 2 &&
                message.TryGetProperty("result", out JsonElement result) &&
                result.TryGetProperty("sessionId", out _))
            {
                _sessionNewCompletion.TrySetResult();
            }
        }

        private void Respond(JsonElement request, string resultJson)
        {
            JsonElement id = request.GetProperty("id");
            string response =
                "{\"jsonrpc\":\"2.0\",\"id\":" +
                id.GetRawText() +
                ",\"result\":" +
                resultJson +
                "}";

            _input.Writer.TryWrite(response);
        }

        private sealed class ChannelTextReader : TextReader
        {
            private readonly ChannelReader<string?> _reader;
            private int _position;
            private string? _pending;

            public ChannelTextReader(ChannelReader<string?> reader)
            {
                _reader = reader;
            }

            public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                while (true)
                {
                    if (!string.IsNullOrEmpty(_pending) && _position < _pending.Length)
                    {
                        int count = Math.Min(buffer.Length, _pending.Length - _position);
                        _pending.AsMemory(_position, count).CopyTo(buffer);
                        _position += count;
                        if (_position >= _pending.Length)
                        {
                            _pending = null;
                            _position = 0;
                        }

                        return count;
                    }

                    try
                    {
                        string? next = await _reader.ReadAsync(cancellationToken);
                        if (next is null)
                        {
                            return 0;
                        }

                        _pending = next + "\n";
                        _position = 0;
                    }
                    catch (ChannelClosedException)
                    {
                        return 0;
                    }
                }
            }

            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                try
                {
                    return await _reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
        }

        private sealed class ScriptedTextWriter : TextWriter
        {
            private readonly ScriptedAcpTransport _transport;

            public ScriptedTextWriter(ScriptedAcpTransport transport)
            {
                _transport = transport;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override Task WriteLineAsync(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _transport.HandleOutputLine(value);
                }

                return Task.CompletedTask;
            }
        }
    }

    private sealed class NonResponsiveAcpTransport
    {
        private readonly Channel<string?> _input = Channel.CreateUnbounded<string?>();
        private readonly Dictionary<string, TaskCompletionSource> _methodWaiters = new(StringComparer.Ordinal);
        private readonly List<JsonElement> _messages = [];

        public NonResponsiveAcpTransport()
        {
            Input = new ChannelTextReader(_input.Reader);
            Output = new SilentTextWriter(this);
        }

        public TextReader Input { get; }

        public IReadOnlyList<JsonElement> Messages => _messages;

        public TextWriter Output { get; }

        public void CompleteInput()
        {
            _input.Writer.TryComplete();
        }

        public ValueTask SendAsync(string line)
        {
            return _input.Writer.WriteAsync(line);
        }

        public Task WaitForMethodAsync(string methodName)
        {
            lock (_methodWaiters)
            {
                if (_messages.Any(message => IsMethod(message, methodName)))
                {
                    return Task.CompletedTask;
                }

                if (!_methodWaiters.TryGetValue(methodName, out TaskCompletionSource? waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _methodWaiters[methodName] = waiter;
                }

                return waiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private void HandleOutputLine(string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement message = document.RootElement.Clone();
            _messages.Add(message);

            if (!message.TryGetProperty("method", out JsonElement method))
            {
                return;
            }

            string? methodName = method.GetString();
            if (string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            lock (_methodWaiters)
            {
                if (_methodWaiters.TryGetValue(methodName, out TaskCompletionSource? waiter))
                {
                    waiter.TrySetResult();
                }
            }
        }

        private sealed class ChannelTextReader : TextReader
        {
            private readonly ChannelReader<string?> _reader;
            private int _position;
            private string? _pending;

            public ChannelTextReader(ChannelReader<string?> reader)
            {
                _reader = reader;
            }

            public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                {
                    return 0;
                }

                while (true)
                {
                    if (!string.IsNullOrEmpty(_pending) && _position < _pending.Length)
                    {
                        int count = Math.Min(buffer.Length, _pending.Length - _position);
                        _pending.AsMemory(_position, count).CopyTo(buffer);
                        _position += count;
                        if (_position >= _pending.Length)
                        {
                            _pending = null;
                            _position = 0;
                        }

                        return count;
                    }

                    try
                    {
                        string? next = await _reader.ReadAsync(cancellationToken);
                        if (next is null)
                        {
                            return 0;
                        }

                        _pending = next + "\n";
                        _position = 0;
                    }
                    catch (ChannelClosedException)
                    {
                        return 0;
                    }
                }
            }

            public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
            {
                try
                {
                    return await _reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    return null;
                }
            }
        }

        private sealed class SilentTextWriter : TextWriter
        {
            private readonly NonResponsiveAcpTransport _transport;

            public SilentTextWriter(NonResponsiveAcpTransport transport)
            {
                _transport = transport;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override Task WriteLineAsync(string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _transport.HandleOutputLine(value);
                }

                return Task.CompletedTask;
            }
        }
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempWorkspace Create()
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "stemcode-acp-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempWorkspace(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
