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

Create a client without any built-in StemCode tools by omitting `UseBuildTool()`.
The agent can answer normally, but it cannot call repository editing, file,
shell, browser, planning, memory, code intelligence, or subagent tools unless
you add tools yourself.

SDK clients start with no configured base system prompt. Add one explicitly
with `WithSystemPrompt(...)`, or opt into StemCode's built-in coding-agent
prompt with `UseStemCodeSystemPrompt()`:

```csharp
await using StemCodeClient client = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey, "claude-opus-4-8")
    .WithSystemPrompt("Follow this product team's engineering conventions.")
    .Build();

await using StemCodeClient stemCodePromptClient = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey, "claude-opus-4-8")
    .UseStemCodeSystemPrompt()
    .Build();
```

```csharp
using StemCode.Sdk;

await using StemCodeClient client = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey, "claude-opus-4-8")
    .Build();

client.AssistantMessageChunkReceived += (_, e) => Console.Write(e.Text);

await client.InitializeAsync();
ConversationTurnResult result = await client.RunTurnAsync("Explain dependency injection in C#.");
Console.WriteLine(result.ResponseText);
```

Build a simple conversation loop by initializing once, then calling
`RunTurnAsync` for each user message. Reusing the same client keeps the session
history alive between turns.

```csharp
using StemCode.Sdk;

await using StemCodeClient client = StemCodeClient.CreateBuilder()
    .UseAnthropic(apiKey, "claude-opus-4-8")
    .UseBuildTool()
    .WithWorkspace("/path/to/repo")
    .Build();

client.AssistantMessageChunkReceived += (_, e) => Console.Write(e.Text);
client.ToolCallsStarted += (_, e) => Console.WriteLine($"Running {e.ToolCalls.Count} tool(s)...");

await client.InitializeAsync();

while (true)
{
    Console.Write("> ");
    string? prompt = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(prompt) ||
        prompt.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    Console.WriteLine();
    await client.RunTurnAsync(prompt);
    Console.WriteLine();
}
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
