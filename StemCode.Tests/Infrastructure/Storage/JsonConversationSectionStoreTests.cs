using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Storage;

namespace StemCode.Tests.Infrastructure.Storage;

public sealed class JsonConversationSectionStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonConversationSectionStoreTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-sessions-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripSectionSnapshot()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonConversationSectionStore sut = new(pathProvider);
        ConversationSectionSnapshot snapshot = new(
            Guid.NewGuid().ToString("D"),
            "Todo App Session",
            new DateTimeOffset(2026, 4, 21, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 21, 1, 5, 0, TimeSpan.Zero),
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini", "gpt-4.1"],
            [
                new ConversationSectionTurn(
                    "build a todo app",
                    "I created the scaffold.",
                    [
                        new ConversationToolCall(
                            "call_1",
                            "file_write",
                            """{ "path": "README.md", "content": "hello" }""")
                    ],
                    ["\u2022 Edited 1 file (+1 -0)"])
            ],
            27,
            new PendingExecutionPlan(
                "plan the todo app",
                "Plan\n1. Inspect\n2. Implement\n3. Validate",
                ["Inspect", "Implement", "Validate"]),
            sessionState: new SessionStateSnapshot(
                [new SessionFileContext(
                    "README.md",
                    "read",
                    new DateTimeOffset(2026, 4, 21, 1, 1, 0, TimeSpan.Zero),
                    "Read 10 characters.")],
                [new SessionEditContext(
                    new DateTimeOffset(2026, 4, 21, 1, 2, 0, TimeSpan.Zero),
                    "file_write (README.md)",
                    ["README.md"],
                    1,
                    0)],
                [new SessionTerminalCommand(
                    new DateTimeOffset(2026, 4, 21, 1, 3, 0, TimeSpan.Zero),
                    "dotnet test",
                    ".",
                    0,
                    "Passed",
                    null)]),
            workspacePath: _tempRoot,
            modelContextWindowTokens: new Dictionary<string, int>
            {
                ["gpt-5-mini"] = 128_000,
                ["gpt-4.1"] = 1_000_000
            });

        await sut.SaveAsync(snapshot, CancellationToken.None);
        ConversationSectionSnapshot? loadedSnapshot = await sut.LoadAsync(snapshot.SectionId, CancellationToken.None);

        loadedSnapshot.Should().NotBeNull();
        loadedSnapshot!.SectionId.Should().Be(snapshot.SectionId);
        loadedSnapshot.Title.Should().Be("Todo App Session");
        loadedSnapshot.ActiveModelId.Should().Be("gpt-5-mini");
        loadedSnapshot.AvailableModelIds.Should().Equal("gpt-5-mini", "gpt-4.1");
        loadedSnapshot.ModelContextWindowTokens.Should().Contain("gpt-5-mini", 128_000);
        loadedSnapshot.ModelContextWindowTokens.Should().Contain("gpt-4.1", 1_000_000);
        loadedSnapshot.ProviderProfile.Should().Be(snapshot.ProviderProfile);
        loadedSnapshot.Turns.Should().ContainSingle();
        loadedSnapshot.Turns[0].UserInput.Should().Be("build a todo app");
        loadedSnapshot.Turns[0].AssistantResponse.Should().Be("I created the scaffold.");
        loadedSnapshot.Turns[0].ToolCalls.Should().ContainSingle();
        loadedSnapshot.Turns[0].ToolCalls[0].Name.Should().Be("file_write");
        loadedSnapshot.Turns[0].ToolCalls[0].ArgumentsJson.Should().Contain("README.md");
        loadedSnapshot.Turns[0].ToolOutputMessages.Should().ContainSingle();
        loadedSnapshot.Turns[0].ToolOutputMessages[0].Should().Be("\u2022 Edited 1 file (+1 -0)");
        loadedSnapshot.TotalEstimatedOutputTokens.Should().Be(27);
        loadedSnapshot.WorkspacePath.Should().Be(Path.GetFullPath(_tempRoot));
        loadedSnapshot.PendingExecutionPlan.Should().NotBeNull();
        loadedSnapshot.PendingExecutionPlan!.SourceUserInput.Should().Be("plan the todo app");
        loadedSnapshot.PendingExecutionPlan.Tasks.Should().Equal("Inspect", "Implement", "Validate");
        loadedSnapshot.SessionState.Files.Should().ContainSingle();
        loadedSnapshot.SessionState.Files[0].Path.Should().Be("README.md");
        loadedSnapshot.SessionState.Edits.Should().ContainSingle();
        loadedSnapshot.SessionState.Edits[0].Description.Should().Be("file_write (README.md)");
        loadedSnapshot.SessionState.TerminalHistory.Should().ContainSingle();
        loadedSnapshot.SessionState.TerminalHistory[0].Command.Should().Be("dotnet test");
    }

    [Fact]
    public async Task LoadAsync_Should_TreatMissingTurnToolCallsAsEmpty()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonConversationSectionStore sut = new(pathProvider);
        string sectionId = Guid.NewGuid().ToString("D");
        string sectionsDirectory = pathProvider.GetSessionsDirectoryPath();
        Directory.CreateDirectory(sectionsDirectory);

        await File.WriteAllTextAsync(
            Path.Combine(sectionsDirectory, $"{sectionId}.json"),
            $$"""
            {
              "sectionId": "{{sectionId}}",
              "title": "Old Section",
              "createdAtUtc": "2026-04-21T01:00:00+00:00",
              "updatedAtUtc": "2026-04-21T01:05:00+00:00",
              "providerProfile": {
                "providerKind": "openAiCompatible",
                "baseUrl": "https://provider.example.com/v1"
              },
              "activeModelId": "gpt-5-mini",
              "availableModelIds": ["gpt-5-mini"],
              "turns": [
                {
                  "userInput": "old prompt",
                  "assistantResponse": "old response"
                }
              ],
              "totalEstimatedOutputTokens": 12
            }
            """,
            CancellationToken.None);

        ConversationSectionSnapshot? loadedSnapshot = await sut.LoadAsync(sectionId, CancellationToken.None);

        loadedSnapshot.Should().NotBeNull();
        loadedSnapshot!.Turns.Should().ContainSingle();
        loadedSnapshot.Turns[0].Status.Should().Be(ConversationTurnStatus.Completed);
        loadedSnapshot.Turns[0].ToolCalls.Should().BeEmpty();
        loadedSnapshot.Turns[0].ToolOutputMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task ListAsync_Should_IgnoreSessionRecordFiles()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonConversationSectionStore sut = new(pathProvider);
        ConversationSectionSnapshot snapshot = new(
            Guid.NewGuid().ToString("D"),
            "Real Section",
            new DateTimeOffset(2026, 4, 21, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 21, 1, 5, 0, TimeSpan.Zero),
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini"],
            [],
            0);
        await sut.SaveAsync(snapshot, CancellationToken.None);

        string sessionsDirectory = pathProvider.GetSessionsDirectoryPath();
        string parentSessionId = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(
            Path.Combine(sessionsDirectory, $"{parentSessionId}.json"),
            $$"""
            {
              "sessionId": "{{parentSessionId}}",
              "createdAtUtc": "2026-04-21T01:00:00+00:00",
              "updatedAtUtc": "2026-04-21T01:05:00+00:00",
              "sectionIds": ["{{snapshot.SectionId}}"],
              "accumulatedContext": {
                "completedSectionSummaries": [],
                "files": [],
                "edits": [],
                "terminalHistory": []
              }
            }
            """,
            CancellationToken.None);

        IReadOnlyList<ConversationSectionSnapshot> snapshots = await sut.ListAsync(CancellationToken.None);
        ConversationSectionSnapshot? parentRecord = await sut.LoadAsync(parentSessionId, CancellationToken.None);

        snapshots.Should().ContainSingle();
        snapshots[0].SectionId.Should().Be(snapshot.SectionId);
        parentRecord.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_PreserveInterruptedTurn()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonConversationSectionStore sut = new(pathProvider);
        ConversationSectionSnapshot snapshot = new(
            Guid.NewGuid().ToString("D"),
            "Interrupted Session",
            new DateTimeOffset(2026, 4, 21, 1, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 4, 21, 1, 5, 0, TimeSpan.Zero),
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "gpt-5-mini",
            ["gpt-5-mini"],
            [
                new ConversationSectionTurn(
                    "Implement the round winner component",
                    status: ConversationTurnStatus.Interrupted,
                    toolCalls:
                    [
                        new ConversationToolCall(
                            "call_1",
                            "file_read",
                            """{ "path": "README.md" }""")
                    ],
                    toolOutputMessages: ["Read README.md"],
                    failureInfo: new ConversationFailureInfo(
                        "provider_request_error",
                        "OpenAI-compatible",
                        "gpt-5-mini",
                        isRetryable: false))
            ],
            12);

        await sut.SaveAsync(snapshot, CancellationToken.None);
        ConversationSectionSnapshot? loadedSnapshot = await sut.LoadAsync(snapshot.SectionId, CancellationToken.None);

        loadedSnapshot.Should().NotBeNull();
        loadedSnapshot!.Turns.Should().ContainSingle();
        loadedSnapshot.Turns[0].Status.Should().Be(ConversationTurnStatus.Interrupted);
        loadedSnapshot.Turns[0].AssistantResponse.Should().BeNull();
        loadedSnapshot.Turns[0].ToolOutputMessages.Should().ContainSingle().Which.Should().Be("Read README.md");
        loadedSnapshot.Turns[0].FailureInfo.Should().NotBeNull();
        loadedSnapshot.Turns[0].FailureInfo!.Category.Should().Be("provider_request_error");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private sealed class StubUserDataPathProvider : IUserDataPathProvider
    {
        private readonly string _root;

        public StubUserDataPathProvider(string root)
        {
            _root = root;
        }

        public string GetConfigurationFilePath()
        {
            return Path.Combine(_root, "agent-profile.json");
        }

        public string GetMcpConfigurationFilePath()
        {
            return Path.Combine(_root, "mcp.toml");
        }

        public string GetLogsDirectoryPath()
        {
            return Path.Combine(_root, "logs");
        }

        public string GetSessionsDirectoryPath()
        {
            return Path.Combine(_root, "sessions");
        }
    }
}
