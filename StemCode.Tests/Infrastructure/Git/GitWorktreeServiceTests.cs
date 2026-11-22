using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Infrastructure.Git;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.Tools;
using System.Diagnostics;

namespace StemCode.Tests.Infrastructure.Git;

public sealed class GitWorktreeServiceTests : IDisposable
{
    private readonly string _repositoryPath;
    private readonly GitWorktreeService _sut = new(new ProcessRunner());

    public GitWorktreeServiceTests()
    {
        _repositoryPath = Path.Combine(Path.GetTempPath(), "stemcode-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repositoryPath);
        RunGit(_repositoryPath, "init");
        RunGit(_repositoryPath, "config", "user.name", "StemCode Tests");
        RunGit(_repositoryPath, "config", "user.email", "tests@stemcode.local");
        Directory.CreateDirectory(Path.Combine(_repositoryPath, "src"));
        File.WriteAllText(Path.Combine(_repositoryPath, "src", "app.txt"), "v1\n");
        RunGit(_repositoryPath, "add", ".");
        RunGit(_repositoryPath, "commit", "-m", "initial");
    }

    [Fact]
    public async Task CreateAsync_Should_CheckOutNewBranchInsideWorkspace_And_KeepMainStatusClean()
    {
        GitWorktreeHandle worktree = await _sut.CreateAsync(_repositoryPath, "Feature A!", CancellationToken.None);

        worktree.Name.Should().Be("feature-a");
        worktree.Branch.Should().Be("stemcode/worktree/feature-a");
        worktree.WorktreePath.Should().Be(
            Path.Combine(Path.GetFullPath(_repositoryPath), ".stemcode", "worktrees", "feature-a"));
        worktree.WorkspaceRoot.Should().Be(worktree.WorktreePath);
        File.ReadAllText(Path.Combine(worktree.WorktreePath, "src", "app.txt")).TrimEnd().Should().Be("v1");
        RunGit(_repositoryPath, "status", "--porcelain").Should().BeEmpty();
    }

    [Fact]
    public async Task FinishAsync_Should_RemoveWorktreeAndBranch_When_NothingChanged()
    {
        GitWorktreeHandle worktree = await _sut.CreateAsync(_repositoryPath, null, CancellationToken.None);

        GitWorktreeOutcome outcome = await _sut.FinishAsync(worktree, CancellationToken.None);

        outcome.Kept.Should().BeFalse();
        Directory.Exists(worktree.WorktreePath).Should().BeFalse();
        RunGit(_repositoryPath, "branch", "--list", worktree.Branch).Should().BeEmpty();
    }

    [Fact]
    public async Task FinishAsync_Should_KeepWorktree_When_FilesChangedOrCommitted()
    {
        GitWorktreeHandle worktree = await _sut.CreateAsync(_repositoryPath, "edits", CancellationToken.None);
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "src", "app.txt"), "v2\n");
        File.WriteAllText(Path.Combine(worktree.WorktreePath, "src", "new.txt"), "new\n");
        RunGit(worktree.WorktreePath, "add", "src/new.txt");
        RunGit(worktree.WorktreePath, "commit", "-m", "add new");

        GitWorktreeOutcome outcome = await _sut.FinishAsync(worktree, CancellationToken.None);

        outcome.Kept.Should().BeTrue();
        outcome.CommitCount.Should().Be(1);
        outcome.ChangedFiles.Should().BeEquivalentTo(["src/app.txt", "src/new.txt"]);
        Directory.Exists(worktree.WorktreePath).Should().BeTrue();
        File.ReadAllText(Path.Combine(_repositoryPath, "src", "app.txt")).TrimEnd().Should().Be("v1");
    }

    [Fact]
    public async Task ListAsync_And_RemoveAsync_Should_ManageOnlyStemCodeWorktrees()
    {
        await _sut.CreateAsync(_repositoryPath, "one", CancellationToken.None);
        await _sut.CreateAsync(_repositoryPath, "two", CancellationToken.None);

        IReadOnlyList<GitWorktreeInfo> worktrees = await _sut.ListAsync(_repositoryPath, CancellationToken.None);
        worktrees.Select(static worktree => worktree.Name).Should().BeEquivalentTo(["one", "two"]);
        worktrees.Should().OnlyContain(static worktree => worktree.Branch!.StartsWith("stemcode/worktree/"));

        await _sut.RemoveAsync(_repositoryPath, "one", CancellationToken.None);

        (await _sut.ListAsync(_repositoryPath, CancellationToken.None))
            .Select(static worktree => worktree.Name)
            .Should()
            .Equal("two");
        RunGit(_repositoryPath, "branch", "--list", "stemcode/worktree/one").Should().BeEmpty();
    }

    [Fact]
    public async Task CreateAsync_Should_MapSubdirectoryWorkspaceIntoWorktree()
    {
        string subdirectory = Path.Combine(_repositoryPath, "src");

        GitWorktreeHandle worktree = await _sut.CreateAsync(subdirectory, "sub", CancellationToken.None);

        worktree.RepositoryRoot.Should().Be(Path.GetFullPath(_repositoryPath));
        worktree.WorktreePath.Should().StartWith(Path.Combine(Path.GetFullPath(subdirectory), ".stemcode", "worktrees"));
        worktree.WorkspaceRoot.Should().Be(Path.Combine(worktree.WorktreePath, "src"));
        File.Exists(Path.Combine(worktree.WorkspaceRoot, "app.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateAsync_Should_Throw_When_NotInGitRepository()
    {
        string plainDirectory = Path.Combine(Path.GetTempPath(), "stemcode-nogit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plainDirectory);
        try
        {
            Func<Task> act = () => _sut.CreateAsync(plainDirectory, null, CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*git repository*");
        }
        finally
        {
            Directory.Delete(plainDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRootScope_Should_OverrideProviderOnlyWithinScope()
    {
        CurrentDirectoryWorkspaceRootProvider provider = new();
        string original = provider.GetWorkspaceRoot();
        string? insideChildTask = null;

        using (WorkspaceRootScope.Enter(_repositoryPath))
        {
            provider.GetWorkspaceRoot().Should().Be(Path.GetFullPath(_repositoryPath));
            await Task.Run(() => insideChildTask = provider.GetWorkspaceRoot());
        }

        insideChildTask.Should().Be(Path.GetFullPath(_repositoryPath));
        provider.GetWorkspaceRoot().Should().Be(original);
    }

    public void Dispose()
    {
        try
        {
            RunGit(_repositoryPath, "worktree", "prune");
            foreach (string file in Directory.EnumerateFiles(_repositoryPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repositoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} failed: {output}{error}");
        return output.Trim();
    }
}
