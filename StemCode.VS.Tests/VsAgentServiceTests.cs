using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using StemCode.VS.Services;
using StemCode.VS.ToolWindows;

namespace StemCode.VS.Tests;

/// <summary>
/// Drives the real VsAgentService (JSON-RPC over stdio) against Fixtures/fake-acp-agent.js.
/// Requires Windows and Node.js, as the Visual Studio extension CI runner has.
/// </summary>
public sealed class VsAgentServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string AgentPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-acp-agent.cmd");

    private readonly StemCodeProcessManager _process = new();
    private readonly VsAgentService _agent;
    private readonly ConcurrentQueue<(string Method, Dictionary<string, object?>? Params)> _notifications = new();
    private int _hostExitedCount;

    public VsAgentServiceTests()
    {
        _agent = new VsAgentService(_process);
        _agent.NotificationReceived += (method, parameters) => _notifications.Enqueue((method, parameters));
        _agent.HostExited += () => Interlocked.Increment(ref _hostExitedCount);
    }

    public async Task InitializeAsync()
    {
        _agent.Start(AgentPath);
        await _agent.InitializeAsync(Path.GetTempPath()).WaitAsync(Timeout);
    }

    public async Task DisposeAsync()
    {
        try { await _agent.StopAsync().WaitAsync(Timeout); } catch { /* already gone */ }
        _agent.Dispose();
    }

    [Fact]
    public async Task Prompt_StreamsChunksAndReturnsMetrics()
    {
        VsAgentService.SessionPromptResponse? response = await _agent.SendPromptAsync("hello").WaitAsync(Timeout);

        response!.StopReason.Should().Be("end_turn");
        response.Metrics!.ElapsedMilliseconds.Should().Be(12.5);
        Texts(VsProtocol.MessageChunk).Should().Equal("Echo: hello");
        _notifications.Select(n => n.Method).Should().Contain(VsProtocol.Ready);
    }

    [Fact]
    public async Task CliExitingMidTurn_FailsThePromptPromptlyAndRaisesHostExited()
    {
        Func<Task> prompt = () => _agent.SendPromptAsync("crash").WaitAsync(Timeout);

        // Before the fix this waited for the 10-minute request timeout.
        await prompt.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exited unexpectedly*");
        await WaitUntilAsync(() => Volatile.Read(ref _hostExitedCount) == 1);
        _agent.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Stop_IsNotReportedAsACrash_AndTheAgentCanStartAgain()
    {
        await _agent.StopAsync().WaitAsync(Timeout);
        _agent.IsRunning.Should().BeFalse();

        _agent.Start(AgentPath);
        await _agent.InitializeAsync(Path.GetTempPath()).WaitAsync(Timeout);
        await _agent.SendPromptAsync("again").WaitAsync(Timeout);

        Texts(VsProtocol.MessageChunk).Should().ContainSingle()
            .Which.Should().Be("Echo: again", "only the new process's output is read");
        await Task.Delay(300);
        Volatile.Read(ref _hostExitedCount).Should().Be(0);
    }

    [Fact]
    public async Task Stop_KillsTheWholeProcessTree()
    {
        // Same shape as an npm install: a cmd.exe shim whose node child ignores stdin closing.
        await _agent.SendPromptAsync("stubborn").WaitAsync(Timeout);
        int nodePid = int.Parse(Texts(VsProtocol.MessageChunk).Single());
        using Process node = Process.GetProcessById(nodePid);

        await _agent.StopAsync().WaitAsync(Timeout);

        node.WaitForExit(5000).Should().BeTrue("the shim's child must not outlive Stop");
    }

    [Fact]
    public async Task Dispose_KillsTheWholeProcessTree()
    {
        await _agent.SendPromptAsync("stubborn").WaitAsync(Timeout);
        using Process node = Process.GetProcessById(int.Parse(Texts(VsProtocol.MessageChunk).Single()));

        _agent.Dispose();

        node.WaitForExit(5000).Should().BeTrue("disposing (Visual Studio closing) must not orphan the CLI");
    }

    [Fact]
    public async Task SessionSwitch_ResetsHistoryThenReplaysUserAndAgentMessages()
    {
        await _agent.SendPromptAsync("/resume").WaitAsync(Timeout);

        _notifications.Select(n => n.Method)
            .Where(m => m is VsProtocol.HistoryReset or VsProtocol.UserMessageChunk or VsProtocol.MessageChunk)
            .Should().Equal(VsProtocol.HistoryReset, VsProtocol.UserMessageChunk, VsProtocol.MessageChunk);
        Texts(VsProtocol.UserMessageChunk).Should().Equal("earlier question");
        Texts(VsProtocol.MessageChunk).Should().Equal("earlier answer");
    }

    [Fact]
    public async Task AvailableCommandsUpdate_IsForwardedAndParses()
    {
        await _agent.SendPromptAsync("commands").WaitAsync(Timeout);

        Dictionary<string, object?> update = _notifications.Single(n => n.Method == VsProtocol.AvailableCommands).Params!;
        List<AvailableCommandEntry> entries = ChatCommands.ParseAvailableCommands((JsonElement)update["availableCommands"]!);

        entries.Select(e => e.Name).Should().Equal("undo", "deploy");
        ChatCommands.Build(entries).Single(c => c.Command == "/deploy").InsertText.Should().Be("/deploy ");
    }

    [Fact]
    public async Task PermissionRequest_RoundTripsTheSelectedOption()
    {
        Task<VsAgentService.SessionPromptResponse?> prompt = _agent.SendPromptAsync("ask");

        await WaitUntilAsync(() => _notifications.Any(n => n.Method == VsProtocol.RequestPermission));
        string requestId = Text(_notifications.Single(n => n.Method == VsProtocol.RequestPermission).Params, "id")!;
        await _agent.ResolveClientRequestAsync(requestId, "selected", null, "allow");

        await prompt.WaitAsync(Timeout);
        Texts(VsProtocol.MessageChunk).Should().Equal("chose allow");
    }

    [Fact]
    public async Task ConcurrentPrompts_AllComplete()
    {
        // Writes are serialized; interleaved lines would corrupt the JSON and hang requests.
        Task<VsAgentService.SessionPromptResponse?>[] prompts = Enumerable.Range(0, 25)
            .Select(i => Task.Run(() => _agent.SendPromptAsync("p" + i)))
            .ToArray();

        await Task.WhenAll(prompts).WaitAsync(Timeout);

        Texts(VsProtocol.MessageChunk).Should().BeEquivalentTo(Enumerable.Range(0, 25).Select(i => "Echo: p" + i));
    }

    private List<string> Texts(string method)
        => _notifications.Where(n => n.Method == method).Select(n => Text(n.Params, "text")!).ToList();

    private static string? Text(Dictionary<string, object?>? parameters, string key)
        => parameters?.TryGetValue(key, out object? value) == true ? value?.ToString() : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(25);
        }
    }
}
