using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Commands;
using StemCode.Application.Models;
using StemCode.Application.Services;
using StemCode.CLI;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Storage;
using StemCode.Sdk.Internal;
using System.Text.Json.Nodes;

namespace StemCode.Tests.Application.Services;

public sealed class OutputStyleServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("stemcode-styles").FullName;
    private readonly string _workspace;
    private readonly TempUserData _userData;

    public OutputStyleServiceTests()
    {
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(Path.Combine(_workspace, ".stemcode", "output-styles"));
        _userData = new TempUserData(Path.Combine(_root, "user"));
    }

    [Fact]
    public void List_Should_IncludeBuiltInsAndCustomWorkspaceStyles()
    {
        File.WriteAllText(
            Path.Combine(_workspace, ".stemcode", "output-styles", "pirate.md"),
            "---\nname: Pirate\ndescription: Talks like a pirate.\n---\nAnswer in pirate speak, matey.\n");

        IReadOnlyList<OutputStyle> styles = CreateService().List();

        styles.Select(static style => style.Name).Should().Contain(["default", "explanatory", "learning", "concise", "Pirate"]);
        OutputStyle pirate = styles.Single(static style => style.Name == "Pirate");
        pirate.Source.Should().Be("workspace");
        pirate.Description.Should().Be("Talks like a pirate.");
        pirate.Prompt.Should().Contain("Answer in pirate speak");
    }

    [Fact]
    public async Task SetActiveAsync_Should_PersistToUserConfig_AndBeLoadedNextTime()
    {
        OutputStyleService first = CreateService();
        first.Active.Name.Should().Be("default");
        first.Active.Prompt.Should().BeNull();

        OutputStyle style = await first.SetActiveAsync("Explanatory", CancellationToken.None);

        style.Name.Should().Be("explanatory");
        JsonNode saved = JsonNode.Parse(File.ReadAllText(_userData.GetConfigurationFilePath()))!;
        saved["Application"]!["Terminal"]!["OutputStyle"]!.GetValue<string>().Should().Be("explanatory");
        CreateService().Active.Name.Should().Be("explanatory");

        await first.SetActiveAsync("default", CancellationToken.None);
        CreateService().Active.Name.Should().Be("default");
    }

    [Fact]
    public async Task SetActiveAsync_Should_RejectUnknownStyle()
    {
        Func<Task> act = () => CreateService().SetActiveAsync("nope", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Available:*explanatory*");
    }

    [Fact]
    public void TerminalSettingsStore_Should_MergeUserAndWorkspaceSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_userData.GetConfigurationFilePath())!);
        File.WriteAllText(
            _userData.GetConfigurationFilePath(),
            """{ "Application": { "Terminal": { "VimMode": true, "OutputStyle": "concise", "StatusLine": { "Command": "echo hi", "RefreshSeconds": 3 } } } }""");
        File.WriteAllText(
            Path.Combine(_workspace, ".stemcode", "agent-profile.json"),
            """{ "application": { "terminal": { "outputStyle": "learning" } } }""");

        TerminalSettings settings = new TerminalSettingsStore(_userData, new FixedWorkspaceRootProvider(_workspace)).Load();

        settings.VimMode.Should().BeTrue();
        settings.OutputStyle.Should().Be("learning");
        settings.StatusLineCommand.Should().Be("echo hi");
        settings.StatusLineRefreshSeconds.Should().Be(3);
    }

    [Fact]
    public async Task OutputStyleCommand_Should_ListAndSwitch()
    {
        OutputStyleService service = CreateService();
        OutputStyleCommandHandler sut = new(service);
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.OpenAiCompatible, "https://provider.example.com/v1"),
            "m",
            ["m"]);

        ReplCommandResult listing = await sut.ExecuteAsync(new ReplCommandContext("output-style", "", [], "/output-style", session), CancellationToken.None);
        ReplCommandResult switched = await sut.ExecuteAsync(new ReplCommandContext("output-style", "concise", ["concise"], "/output-style concise", session), CancellationToken.None);

        listing.Message.Should().Contain("* default").And.Contain("learning");
        switched.Message.Should().Contain("Output style set to 'concise'");
        service.Active.Name.Should().Be("concise");
    }

    [Fact]
    public async Task CustomStatusLine_Should_ReturnFirstLineAndReceiveJsonInput()
    {
        string command = OperatingSystem.IsWindows()
            ? "findstr \"session_id\""
            : "grep -o session_id";
        string input = CustomStatusLine.BuildInput("abc", _workspace, "claude-sonnet-5-5", "Anthropic", "build", "on", null, 200000, "StemCode 1.0");

        (string? text, string? error) = await CustomStatusLine.RunAsync(command, input);

        error.Should().BeNull();
        text.Should().Contain("session_id");
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private OutputStyleService CreateService()
    {
        IWorkspaceRootProvider workspace = new FixedWorkspaceRootProvider(_workspace);
        return new OutputStyleService(new TerminalSettingsStore(_userData, workspace), _userData, workspace);
    }

    private sealed class TempUserData(string directory) : IUserDataPathProvider
    {
        public string GetConfigurationFilePath() => Path.Combine(directory, "agent-profile.json");

        public string GetMcpConfigurationFilePath() => GetConfigurationFilePath();

        public string GetLogsDirectoryPath() => Path.Combine(directory, "logs");

        public string GetSessionsDirectoryPath() => Path.Combine(directory, "sessions");
    }
}
