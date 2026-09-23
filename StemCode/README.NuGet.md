# StemCode

`StemCode` provides the core libraries and application services behind StemCode, a local AI coding agent built for repository-aware development workflows.

- Repository: https://github.com/rizwan3d/StemCode
- Documentation: https://github.com/rizwan3d/StemCode/blob/master/docs/documentation.md
- Releases: https://github.com/rizwan3d/StemCode/releases/latest

If you want the end-user command-line experience, install the `stemcode` CLI from the release installers:

- macOS / Linux: `curl -fsSL https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.sh | bash`
- Windows PowerShell: `irm https://raw.githubusercontent.com/rizwan3d/StemCode/master/scripts/install.ps1 | iex`

See the [releases page](https://github.com/rizwan3d/StemCode/releases/latest) for all download options.

## Embed the SDK

The `StemCode.Sdk` namespace lets you drive the coding agent from your own
application (an app builder, a server, a bot, automation) without implementing
the interactive console UI. Configure a provider and model with the fluent
builder, then run turns and subscribe to progress events. Provider credentials
are held in memory only — embedding the SDK never touches the machine-wide
StemCode configuration.

```csharp
using StemCode.Sdk;

await using StemCodeClient client = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey, "claude-opus-4-8")   // or UseOpenAi / UseOllama / UseOpenAiCompatible / ...
    .UseBuildTool()                            // full coding-agent tool bundle
    .WithWorkspace("/path/to/repo")
    .AutoApproveTools()                          // for trusted / sandboxed automation
    .Build();

// Surface progress in your UI (final answer is still returned from RunTurnAsync).
client.AssistantMessageChunkReceived += (_, e) => Console.Write(e.Text);
client.ToolCallsStarted += (_, e) => Console.WriteLine($"Running {e.ToolCalls.Count} tool(s)...");
client.StatusMessage   += (_, e) => Console.WriteLine($"[{e.Severity}] {e.Message}");

await client.InitializeAsync();
ConversationTurnResult result = await client.RunTurnAsync("Build a TODO app");
Console.WriteLine(result.ResponseText);
```

Extend it with your own tools and services:

```csharp
StemCodeClient client = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey)
    .UseBuildTool()                            // alternative to Semantic Kernel-style tool orchestration
    .AddTool(new MyDeployTool())                 // custom ITool the agent can call
    .AddMcpServer(new BackendMcpServerConfiguration("docs") { Url = "https://..." })
    .ConfigureServices(services => { /* override or add any DI service */ })
    .Build();
```

The CLI and desktop apps continue to use the same core through their existing
entry points; the SDK is an additive layer on top.
