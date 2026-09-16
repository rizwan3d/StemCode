using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Tools.Models;
using StemCode.Infrastructure.Storage;

namespace StemCode.Tests.Infrastructure.Storage;

public sealed class WorkspaceCodebaseIndexServiceTests
{
    [Fact]
    public async Task SearchAsync_Should_BuildAndSearchLocalIndex()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "src", "ServiceRegistry.cs"),
            """
            namespace Sample;

            public sealed class ServiceRegistry
            {
                public void ConfigureServices()
                {
                }
            }
            """);

        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        var result = await sut.SearchAsync(
            "configure services registry",
            limit: 5,
            includeSnippets: true,
            CancellationToken.None);

        result.IndexWasUpdated.Should().BeTrue();
        result.Matches.Should().ContainSingle(match => match.Path == "src/ServiceRegistry.cs");
        result.Matches[0].Symbols.Should().Contain("ServiceRegistry");
        result.Matches[0].Snippets.Should().Contain(snippet => snippet.Text.Contains("ConfigureServices"));
        File.Exists(Path.Combine(workspace.Path, ".stemcode", "cache", "codebase-index.zvec"))
            .Should()
            .BeTrue();
        File.Exists(Path.Combine(workspace.Path, ".stemcode", "cache", "codebase-index.json"))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task BuildAsync_Should_PersistTinyE5Int8EmbeddingsInZvecInsteadOfJson()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "service.cs"),
            """
            public sealed class ServiceRegistry
            {
                public void ConfigureServices()
                {
                }
            }
            """);

        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        await sut.BuildAsync(force: false, CancellationToken.None);

        string indexPath = Path.Combine(workspace.Path, ".stemcode", "cache", "codebase-index.zvec");
        await using FileStream stream = File.OpenRead(indexPath);
        CodebaseIndexDocument index = await ZvecCodebaseIndexStore.LoadAsync(stream, CancellationToken.None);

        index.EmbeddingModelId.Should().Be("GrowBitLabs/tinye5");
        index.EmbeddingModelUrl.Should().Be("https://huggingface.co/GrowBitLabs/tinye5/tree/int8-onnx");
        index.EmbeddingQuantization.Should().Be("int8-onnx");
        index.EmbeddingDimensions.Should().Be(384);
        index.Files.Should().ContainSingle();
        index.Files[0].Embedding.Should().HaveCount(384);
        index.Files[0].Embedding
            .Select(static value => (int)value)
            .Should()
            .OnlyContain(value => value >= sbyte.MinValue && value <= sbyte.MaxValue);
        File.Exists(Path.Combine(workspace.Path, ".stemcode", "cache", "codebase-index.json"))
            .Should()
            .BeFalse();
    }

    [Fact]
    public async Task BuildAsync_Should_RespectIgnoreFilesAndDefaultGeneratedDirectories()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "ignored"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "node_modules", "pkg"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".stemcode"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, ".stemcode", ".stemcodeignore"),
            "ignored/\nsecret.txt\n");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "src", "Visible.cs"),
            "public sealed class VisibleFeature {}");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "ignored", "Hidden.cs"),
            "public sealed class HiddenFeature {}");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "node_modules", "pkg", "Package.js"),
            "export function packageFeature() {}");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "secret.txt"),
            "secret feature");

        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        await sut.BuildAsync(force: false, CancellationToken.None);
        var listed = await sut.ListAsync(limit: 100, CancellationToken.None);

        listed.Files.Should().Contain("src/Visible.cs");
        listed.Files.Should().NotContain(path => path.Contains("Hidden.cs", StringComparison.OrdinalIgnoreCase));
        listed.Files.Should().NotContain(path => path.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        listed.Files.Should().NotContain("secret.txt");
    }

    [Fact]
    public async Task SearchAsync_Should_RefreshIncrementally_When_FilesChange()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "first.cs"),
            "public sealed class FirstFeature {}");
        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        await sut.BuildAsync(force: false, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "second.cs"),
            "public sealed class SecondFeature {}");

        var staleStatus = await sut.GetStatusAsync(CancellationToken.None);
        staleStatus.IsStale.Should().BeTrue();
        staleStatus.NewFileCount.Should().Be(1);

        // The incremental rebuild is triggered by the file-system watcher, whose events can
        // arrive a few milliseconds after the write. Poll the search until the new file is
        // reflected so the assertion is not racy on slow CI filesystems.
        CodebaseIndexSearchResult search = await PollUntilAsync(
            () => sut.SearchAsync(
                "SecondFeature",
                limit: 5,
                includeSnippets: false,
                CancellationToken.None),
            result => result.Matches.Any(match => match.Path == "second.cs"),
            TimeSpan.FromSeconds(3));

        search.IndexWasUpdated.Should().BeTrue();
        search.Matches.Should().ContainSingle(match => match.Path == "second.cs");
        CodebaseIndexStatusResult freshStatus = await PollUntilAsync(
            () => sut.GetStatusAsync(CancellationToken.None),
            result => !result.IsStale,
            TimeSpan.FromSeconds(3));
        freshStatus.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_Should_IndexSemanticSymbolsDependenciesCallsAndOwners()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".github"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, ".github", "CODEOWNERS"),
            "src/* @web-team\n");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "src", "lib.ts"),
            """
            export function helper() {
                return true;
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "src", "main.ts"),
            """
            import { helper } from "./lib";

            export function run() {
                return helper();
            }
            """);

        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        CodebaseIndexBuildResult build = await sut.BuildAsync(force: false, CancellationToken.None);
        CodebaseIndexSearchResult search = await sut.SearchAsync(
            "run helper web",
            limit: 10,
            includeSnippets: false,
            CancellationToken.None);

        build.Stats.SemanticSymbolCount.Should().BeGreaterThanOrEqualTo(2);
        build.Stats.DependencyEdgeCount.Should().BeGreaterThanOrEqualTo(1);
        build.Stats.CallEdgeCount.Should().BeGreaterThanOrEqualTo(1);
        build.Stats.OwnedFileCount.Should().Be(2);

        CodebaseIndexSearchMatch mainMatch = search.Matches.Single(match => match.Path == "src/main.ts");
        mainMatch.SemanticSymbols.Should().Contain(symbol => symbol.Name == "run" && symbol.Kind == "function");
        mainMatch.Dependencies.Should().Contain(dependency =>
            dependency.Target == "./lib" &&
            dependency.ResolvedPaths.Contains("src/lib.ts"));
        mainMatch.OutgoingCalls.Should().Contain(call =>
            call.CallerSymbol == "run" &&
            call.CalleeSymbol == "helper" &&
            call.CalleePath == "src/lib.ts");
        mainMatch.Owners.Should().Contain("@web-team");

        CodebaseIndexSearchMatch libMatch = search.Matches.Single(match => match.Path == "src/lib.ts");
        libMatch.IncomingCalls.Should().Contain(call =>
            call.CallerSymbol == "run" &&
            call.CallerPath == "src/main.ts" &&
            call.CalleeSymbol == "helper");
    }

    [Fact]
    public async Task GetStatusAsync_Should_ReportStaleWarnings()
    {
        using TempWorkspace workspace = TempWorkspace.Create();
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "first.cs"),
            "public sealed class FirstFeature {}");
        WorkspaceCodebaseIndexService sut = CreateService(workspace.Path);

        await sut.BuildAsync(force: false, CancellationToken.None);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.Path, "second.cs"),
            "public sealed class SecondFeature {}");

        CodebaseIndexStatusResult status = await sut.GetStatusAsync(CancellationToken.None);

        status.IsStale.Should().BeTrue();
        status.Warnings.Should().Contain(warning => warning.Contains("Index is stale", StringComparison.Ordinal));
    }

    private static WorkspaceCodebaseIndexService CreateService(string workspacePath)
    {
        return new WorkspaceCodebaseIndexService(
            new FixedWorkspaceRootProvider(workspacePath),
            new TestCodebaseEmbeddingProvider());
    }

    private sealed class TestCodebaseEmbeddingProvider : ICodebaseEmbeddingProvider
    {
        public CodebaseEmbeddingMetadata Metadata { get; } = new(
            "GrowBitLabs/tinye5",
            "TinyE5-L6-384",
            "75a45e2568c14df937ded5379b877c1b37b0b998",
            "https://huggingface.co/GrowBitLabs/tinye5/tree/int8-onnx",
            "int8-onnx",
            384);

        public Task<sbyte[]> CreateQueryEmbeddingAsync(
            string workspaceRoot,
            string query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateEmbedding(query));
        }

        public Task<sbyte[]> CreatePassageEmbeddingAsync(
            string workspaceRoot,
            CodebaseEmbeddingPassage passage,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(CreateEmbedding(passage.ToModelInputText()));
        }

        public void Dispose()
        {
        }

        private static sbyte[] CreateEmbedding(string text)
        {
            sbyte[] embedding = new sbyte[384];
            foreach (char character in text.ToLowerInvariant())
            {
                if (!char.IsLetterOrDigit(character))
                {
                    continue;
                }

                int index = character % embedding.Length;
                embedding[index] = (sbyte)Math.Clamp(embedding[index] + 1, sbyte.MinValue, sbyte.MaxValue);
            }

            return embedding;
        }
    }

    private sealed class FixedWorkspaceRootProvider : IWorkspaceRootProvider
    {
        private readonly string _workspacePath;

        public FixedWorkspaceRootProvider(string workspacePath)
        {
            _workspacePath = workspacePath;
        }

        public string GetWorkspaceRoot()
        {
            return _workspacePath;
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
                "StemCode.Tests",
                Guid.NewGuid().ToString("N"));
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

    private static async Task<T> PollUntilAsync<T>(
        Func<Task<T>> action,
        Func<T, bool> predicate,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            T result = await action();
            if (predicate(result))
            {
                return result;
            }

            if (DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(50);
        }
    }
}
