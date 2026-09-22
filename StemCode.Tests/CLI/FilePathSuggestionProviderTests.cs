using FluentAssertions;
using StemCode.CLI;

namespace StemCode.Tests.CLI;

public sealed class FilePathSuggestionProviderTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _homeRoot;

    public FilePathSuggestionProviderTests()
    {
        _workspaceRoot = Path.Combine(
            Path.GetTempPath(),
            "stemcode-path-suggestions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);

        _homeRoot = Path.Combine(
            Path.GetTempPath(),
            "stemcode-path-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_homeRoot);
    }

    [Fact]
    public void GetSuggestions_Should_SuggestWorkspaceFilesForReadCommand()
    {
        WriteFile("README.md", "hello");
        WriteFile("docs/guide.md", "hello");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/read R",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("README.md");
        suggestions[0].CompletedInput.Should().Be("/read README.md");
        suggestions[0].IsDirectory.Should().BeFalse();
    }

    [Fact]
    public void GetSuggestions_Should_SuggestDirectoriesBeforeFiles()
    {
        WriteFile("docs/guide.md", "hello");
        WriteFile("docs.md", "hello");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/read do",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Equal("docs/", "docs.md");
    }

    [Fact]
    public void GetSuggestions_Should_FilterJsonFilesForImportCommand()
    {
        WriteFile("session.json", "{}");
        WriteFile("session.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/import se",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Equal("session.json");
    }

    [Fact]
    public void GetSuggestions_Should_SuggestWorkspaceFilesForTrajectoryExportCommand()
    {
        WriteFile("exports/session.trajectory.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/export trajectory exports/se",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("exports/session.trajectory.html");
        suggestions[0].CompletedInput.Should().Be("/export trajectory exports/session.trajectory.html");
        suggestions[0].IsDirectory.Should().BeFalse();
    }

    [Fact]
    public void GetSuggestions_Should_RejectPathsThatEscapeWorkspace()
    {
        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/read ../",
            maxCount: 8);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void GetSuggestions_Should_SkipBuildAndRuntimeDirectories()
    {
        WriteFile("bin/output.dll", "binary");
        WriteFile(".stemcode/cache/index.json", "{}");
        WriteFile(".stemcode/agent-profile.json", "{}");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "/read .stemcode/",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Equal(".stemcode/agent-profile.json");
    }

    [Fact]
    public void GetSuggestions_Should_CompleteDirectoryTokenForShellCommand()
    {
        WriteFile("src/index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!cd ./sr",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("./src/");
        suggestions[0].CompletedInput.Should().Be("!cd ./src/");
        suggestions[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void GetSuggestions_Should_CompleteFileTokenForShellCommand()
    {
        WriteFile("index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!stemcode in",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("index.html");
        suggestions[0].CompletedInput.Should().Be("!stemcode index.html");
        suggestions[0].IsDirectory.Should().BeFalse();
    }

    [Fact]
    public void GetSuggestions_Should_PreserveTypedDirectoryPrefixForShellCommand()
    {
        WriteFile("src/components/button.tsx", "export {}");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!cat src/comp",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("src/components/");
        suggestions[0].CompletedInput.Should().Be("!cat src/components/");
    }

    [Fact]
    public void GetSuggestions_Should_CompleteTokenForBackgroundShellCommand()
    {
        WriteFile("server.js", "//");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!!node ser",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].CompletedInput.Should().Be("!!node server.js");
    }

    [Fact]
    public void GetSuggestions_Should_ListWorkspaceWhenShellCommandHasTrailingSpace()
    {
        WriteFile("README.md", "hello");
        WriteFile("docs/guide.md", "hello");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!ls ",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Equal("./", "docs/", "README.md");
    }

    [Fact]
    public void GetSuggestions_Should_SuggestCurrentDirectoryForShellDotToken()
    {
        WriteFile("README.md", "hello");
        WriteFile("docs/guide.md", "hello");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!!ls .",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Equal("./");

        suggestions[0].CompletedInput.Should().Be("!!ls ./");
        suggestions[0].Description.Should().Be("Current directory");
        suggestions[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void GetSuggestions_Should_NotCompleteShellCommandName()
    {
        WriteFile("cdrom.txt", "data");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!cd",
            maxCount: 8);

        suggestions.Should().BeEmpty();
    }


    [Fact]
    public void GetSuggestions_Should_SuggestWorkspaceFilesForPlainRelativePath()
    {
        WriteFile("README.md", "hello");
        WriteFile("docs/guide.md", "hello");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "./",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Contain("./README.md");
    }

    [Fact]
    public void GetSuggestions_Should_CompleteDirectoryForPlainRelativePath()
    {
        WriteFile("src/index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "./sr",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("./src/");
        suggestions[0].CompletedInput.Should().Be("./src/");
        suggestions[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void GetSuggestions_Should_NotTriggerForPlainNonPathInput()
    {
        WriteFile("src/index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "src/",
            maxCount: 8);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void GetSuggestions_Should_CompleteLastPathTokenInCommandLine()
    {
        WriteFile("src/index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "cd ./sr",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("./src/");
        suggestions[0].CompletedInput.Should().Be("cd ./src/");
        suggestions[0].IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void GetSuggestions_Should_CompleteTildeTokenAfterCommandWord()
    {
        WriteHomeFile("notes.txt", "x");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "run ~/no",
            maxCount: 8,
            homeDirectory: _homeRoot);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("~/notes.txt");
        suggestions[0].CompletedInput.Should().Be("run ~/notes.txt");
    }

    [Fact]
    public void GetSuggestions_Should_NotTriggerWhenLastTokenIsNotAPath()
    {
        WriteFile("src/index.html", "<html></html>");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "cd src",
            maxCount: 8);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void GetSuggestions_Should_CompleteLastPathTokenBeforeCaret()
    {
        WriteFile("docs/guide.md", "hello");

        // The caller passes only the text before the caret, so a path token that is the
        // last token before the caret completes even when more text follows it on the line.
        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "open ./do",
            maxCount: 8);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Contain("./docs/");
    }

    [Fact]
    public void GetSuggestions_Should_CompletePathTypedDirectlyAfterBang()
    {
        WriteFile("build.sh", "x");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!./bu",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("./build.sh");
        suggestions[0].CompletedInput.Should().Be("!./build.sh");
    }

    [Fact]
    public void GetSuggestions_Should_CompletePathTypedDirectlyAfterBackgroundBang()
    {
        WriteFile("build.sh", "x");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!!./bu",
            maxCount: 8);

        suggestions.Should().ContainSingle();
        suggestions[0].CompletedInput.Should().Be("!!./build.sh");
    }

    [Fact]
    public void GetSuggestions_Should_NotEscapeWorkspaceForPlainParentPath()
    {
        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "../",
            maxCount: 8);

        suggestions.Should().BeEmpty();
    }

    [Fact]
    public void GetSuggestions_Should_SuggestHomeFilesForTildeInNormalInput()
    {
        WriteHomeFile("notes.txt", "x");
        WriteHomeFile("projects/readme.md", "y");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "~/",
            maxCount: 8,
            homeDirectory: _homeRoot);

        suggestions.Select(suggestion => suggestion.DisplayPath)
            .Should()
            .Contain("~/notes.txt");
    }

    [Fact]
    public void GetSuggestions_Should_CompleteHomeFileForTildeAfterBang()
    {
        WriteHomeFile("notes.txt", "x");

        IReadOnlyList<FilePathSuggestion> suggestions = FilePathSuggestionProvider.GetSuggestions(
            _workspaceRoot,
            "!~/no",
            maxCount: 8,
            homeDirectory: _homeRoot);

        suggestions.Should().ContainSingle();
        suggestions[0].DisplayPath.Should().Be("~/notes.txt");
        suggestions[0].CompletedInput.Should().Be("!~/notes.txt");
    }


    public void Dispose()
    {
        if (Directory.Exists(_workspaceRoot))
        {
            Directory.Delete(_workspaceRoot, recursive: true);
        }

        if (Directory.Exists(_homeRoot))
        {
            Directory.Delete(_homeRoot, recursive: true);
        }
    }

    private void WriteFile(string relativePath, string content)
    {
        string path = Path.Combine(_workspaceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteHomeFile(string relativePath, string content)
    {
        string path = Path.Combine(_homeRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
