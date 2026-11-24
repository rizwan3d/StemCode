using System.Text.Json;
using FluentAssertions;
using StemCode.VS.ToolWindows;

namespace StemCode.VS.Tests;

public sealed class ChatCommandsTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Build_WithoutServerCommands_UsesStaticCatalogPlusLocalCommands()
    {
        IReadOnlyList<ChatCommandSuggestion> commands = ChatCommands.Build([]);

        commands.Select(c => c.Command).Should().Contain(["/undo", "/doctor", "/trajectory", "/voice", "/voice setup", "/clear"]);
        commands.Select(c => c.Command).Should().OnlyHaveUniqueItems();
        commands.Select(c => c.Command).Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Fact]
    public void Build_WithServerCommands_ReplacesStaticCatalogAndMarksCustomCommands()
    {
        AvailableCommandEntry[] server =
        [
            new("undo", "Roll back.", "/undo", null, false),
            new("deploy", "Ship it.", "/deploy <env>", null, true),
        ];

        IReadOnlyList<ChatCommandSuggestion> commands = ChatCommands.Build(server);

        commands.Select(c => c.Command).Should().Contain(["/undo", "/deploy"]);
        commands.Select(c => c.Command).Should().NotContain("/doctor", "the live list replaces the fallback catalog");
        ChatCommandSuggestion deploy = commands.Single(c => c.Command == "/deploy");
        deploy.Description.Should().Be("Ship it. (custom)");
        deploy.InsertText.Should().Be("/deploy ");
        deploy.TakesArguments.Should().BeTrue();
    }

    [Fact]
    public void Build_LocalCommandsWinOverServerCommandsWithTheSameName()
    {
        IReadOnlyList<ChatCommandSuggestion> commands = ChatCommands.Build([new("copy", "Server copy.", "/copy", null, false)]);

        commands.Single(c => c.Command == "/copy").Description.Should().Be("Copy the last agent message to the clipboard.");
    }

    [Fact]
    public void ParseAvailableCommands_ReadsNameUsageHintAndCustomFlag()
    {
        JsonElement list = Json("""
            [
              { "name": "fork", "description": "Fork.", "usage": "/fork [turn-number]", "custom": false },
              { "name": "/review", "description": "Review.", "input": { "hint": "<path>" }, "custom": true },
              { "name": "has space" },
              { "description": "no name" },
              "not an object"
            ]
            """);

        List<AvailableCommandEntry> entries = ChatCommands.ParseAvailableCommands(list);

        entries.Should().HaveCount(2);
        entries[0].Should().Be(new AvailableCommandEntry("fork", "Fork.", "/fork [turn-number]", null, false));
        entries[1].Name.Should().Be("review", "a leading slash is stripped");
        entries[1].Usage.Should().Be("/review <path>", "usage falls back to name plus input hint");
        entries[1].InputHint.Should().Be("<path>");
        entries[1].Custom.Should().BeTrue();
    }

    [Fact]
    public void ParseAvailableCommands_IgnoresNonArrays()
    {
        ChatCommands.ParseAvailableCommands(Json("{}")).Should().BeEmpty();
    }

    [Fact]
    public void ServerCommandWithInputHint_IsPrefilledRatherThanSent()
    {
        AvailableCommandEntry entry = ChatCommands.ParseAvailableCommand(Json("""{ "name": "import", "input": { "hint": "<json-path>" } }"""))!;

        ChatCommandSuggestion suggestion = ChatCommands.Build([entry]).Single(c => c.Command == "/import");

        suggestion.InsertText.Should().Be("/import ");
    }

    [Theory]
    [InlineData("/copy", LocalChatCommand.Copy)]
    [InlineData("  /COPY  ", LocalChatCommand.Copy)]
    [InlineData("/exit", LocalChatCommand.Exit)]
    [InlineData("/traj", LocalChatCommand.Trajectory)]
    [InlineData("/trajectory", LocalChatCommand.Trajectory)]
    [InlineData("/voice", LocalChatCommand.Voice)]
    [InlineData("/voice   setup", LocalChatCommand.VoiceSetup)]
    [InlineData("/voice update", LocalChatCommand.VoiceUpdate)]
    [InlineData("/clear", LocalChatCommand.Clear)]
    [InlineData("/new", LocalChatCommand.New)]
    [InlineData("/models", LocalChatCommand.Models)]
    [InlineData("/export html", LocalChatCommand.None)]
    [InlineData("/copy now", LocalChatCommand.None)]
    [InlineData("copy", LocalChatCommand.None)]
    public void ParseLocal_MapsTerminalOnlyCommands(string text, LocalChatCommand expected)
    {
        ChatCommands.ParseLocal(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("/export", true)]
    [InlineData("/export html C:\\out.html", true)]
    [InlineData("/EXPORT json", true)]
    [InlineData("/exports", false)]
    [InlineData("export", false)]
    public void IsExportCommand(string text, bool expected)
    {
        ChatCommands.IsExportCommand(text).Should().Be(expected);
    }

    [Fact]
    public void ParseExportedFile_ReadsFormatAndPath()
    {
        (string Format, string FilePath)? result = ChatCommands.ParseExportedFile(
            "Some preamble\r\nExported session as html:\r\n  C:\\Users\\me\\session.html  \r\n");

        result.Should().NotBeNull();
        result!.Value.Format.Should().Be("HTML");
        result.Value.FilePath.Should().Be("C:\\Users\\me\\session.html");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Export failed: disk full")]
    public void ParseExportedFile_ReturnsNullWithoutAnExportLine(string? output)
    {
        ChatCommands.ParseExportedFile(output).Should().BeNull();
    }

    [Fact]
    public void Match_FiltersByPrefixIgnoringCase()
    {
        ChatCommands.Match(ChatCommands.All, "/VOI").Select(c => c.Command)
            .Should().BeEquivalentTo(["/voice", "/voice setup", "/voice update"]);
    }
}
