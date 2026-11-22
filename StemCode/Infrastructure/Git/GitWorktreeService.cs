using StemCode.Application.Abstractions;
using StemCode.Infrastructure.Secrets;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StemCode.Infrastructure.Git;

/// <summary>
/// Manages isolated git worktrees under <c>.stemcode/worktrees/&lt;name&gt;</c>
/// inside the workspace, each on its own <c>stemcode/worktree/&lt;name&gt;</c> branch.
/// Keeping them inside the workspace lets the parent agent read and diff the
/// results with its normal tools; a <c>.gitignore</c> in the worktrees folder
/// keeps them out of the main checkout's status.
/// </summary>
internal sealed class GitWorktreeService : IGitWorktreeService
{
    internal const string WorktreesRelativeDirectory = ".stemcode/worktrees";
    internal const string BranchPrefix = "stemcode/worktree/";

    private const int MaxNameLength = 48;
    private const int MaxChangedFiles = 200;

    private readonly IProcessRunner _processRunner;

    // git worktree add/remove and branch updates take repository-wide locks, so
    // parallel subagents must not run them at the same time.
    private readonly SemaphoreSlim _gitLock = new(1, 1);

    public GitWorktreeService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public Task<GitWorktreeHandle> CreateAsync(
        string workspaceRoot,
        string? name,
        CancellationToken cancellationToken)
    {
        return WithGitLockAsync(() => CreateCoreAsync(workspaceRoot, name, cancellationToken), cancellationToken);
    }

    public Task<GitWorktreeOutcome> FinishAsync(
        GitWorktreeHandle worktree,
        CancellationToken cancellationToken)
    {
        return WithGitLockAsync(() => FinishCoreAsync(worktree, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<GitWorktreeInfo>> ListAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        return WithGitLockAsync(() => ListCoreAsync(workspaceRoot, cancellationToken), cancellationToken);
    }

    public async Task RemoveAsync(
        string workspaceRoot,
        string name,
        CancellationToken cancellationToken)
    {
        _ = await WithGitLockAsync(
            async () =>
            {
                await RemoveCoreAsync(workspaceRoot, name, cancellationToken);
                return true;
            },
            cancellationToken);
    }

    private async Task<T> WithGitLockAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gitLock.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _gitLock.Release();
        }
    }

    private async Task<GitWorktreeHandle> CreateCoreAsync(
        string workspaceRoot,
        string? name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        string repositoryRoot = await GetRepositoryRootAsync(fullWorkspaceRoot, cancellationToken);

        ProcessExecutionResult headResult = await RunGitAsync(
            repositoryRoot,
            ["rev-parse", "--verify", "HEAD"],
            cancellationToken);
        if (headResult.ExitCode != 0 || string.IsNullOrWhiteSpace(headResult.StandardOutput))
        {
            throw new InvalidOperationException(
                "Cannot create a worktree: the repository has no commits yet. Make an initial commit first.");
        }

        string baseCommit = headResult.StandardOutput.Trim();
        string worktreeName = string.IsNullOrWhiteSpace(name)
            ? CreateDefaultName()
            : SanitizeName(name);
        string worktreesDirectory = GetWorktreesDirectory(fullWorkspaceRoot);
        string worktreePath = Path.Combine(worktreesDirectory, worktreeName);
        if (Directory.Exists(worktreePath))
        {
            throw new InvalidOperationException(
                $"A worktree named '{worktreeName}' already exists at {worktreePath}.");
        }

        EnsureWorktreesDirectory(worktreesDirectory);

        string branch = BranchPrefix + worktreeName;
        ProcessExecutionResult addResult = await RunGitAsync(
            repositoryRoot,
            ["worktree", "add", "-b", branch, worktreePath, baseCommit],
            cancellationToken);
        if (addResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git worktree add failed: {FirstNonEmpty(addResult.StandardError, addResult.StandardOutput)}");
        }

        string relativeWorkspace = Path.GetRelativePath(repositoryRoot, fullWorkspaceRoot);
        string worktreeWorkspaceRoot = string.Equals(relativeWorkspace, ".", StringComparison.Ordinal)
            ? worktreePath
            : Path.GetFullPath(Path.Combine(worktreePath, relativeWorkspace));
        if (!Directory.Exists(worktreeWorkspaceRoot))
        {
            // The workspace folder is not tracked in git (for example it only holds
            // untracked files), so the worktree has no copy of it.
            Directory.CreateDirectory(worktreeWorkspaceRoot);
        }

        return new GitWorktreeHandle(
            worktreeName,
            repositoryRoot,
            worktreePath,
            worktreeWorkspaceRoot,
            branch,
            baseCommit);
    }

    private async Task<GitWorktreeOutcome> FinishCoreAsync(
        GitWorktreeHandle worktree,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(worktree);

        if (!Directory.Exists(worktree.WorktreePath))
        {
            return new GitWorktreeOutcome(worktree, Kept: false, [], 0);
        }

        SortedSet<string> changedFiles = new(StringComparer.Ordinal);

        ProcessExecutionResult statusResult = await RunGitAsync(
            worktree.WorktreePath,
            ["status", "--porcelain", "--untracked-files=all"],
            cancellationToken);
        foreach (string path in ParsePorcelainPaths(statusResult.StandardOutput))
        {
            changedFiles.Add(path);
        }

        int commitCount = 0;
        ProcessExecutionResult countResult = await RunGitAsync(
            worktree.WorktreePath,
            ["rev-list", "--count", $"{worktree.BaseCommit}..HEAD"],
            cancellationToken);
        if (countResult.ExitCode == 0 &&
            int.TryParse(countResult.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedCount))
        {
            commitCount = parsedCount;
        }

        if (commitCount > 0)
        {
            ProcessExecutionResult diffResult = await RunGitAsync(
                worktree.WorktreePath,
                ["diff", "--name-only", worktree.BaseCommit, "HEAD"],
                cancellationToken);
            foreach (string line in SplitLines(diffResult.StandardOutput))
            {
                changedFiles.Add(line);
            }
        }

        if (statusResult.ExitCode == 0 && changedFiles.Count == 0 && commitCount == 0)
        {
            await RemoveWorktreeAndBranchAsync(worktree.RepositoryRoot, worktree.WorktreePath, worktree.Branch, cancellationToken);
            return new GitWorktreeOutcome(worktree, Kept: false, [], 0);
        }

        return new GitWorktreeOutcome(
            worktree,
            Kept: true,
            changedFiles.Take(MaxChangedFiles).ToArray(),
            commitCount);
    }

    private async Task<IReadOnlyList<GitWorktreeInfo>> ListCoreAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        string repositoryRoot = await GetRepositoryRootAsync(fullWorkspaceRoot, cancellationToken);
        string worktreesDirectory = GetWorktreesDirectory(fullWorkspaceRoot);

        ProcessExecutionResult result = await RunGitAsync(
            repositoryRoot,
            ["worktree", "list", "--porcelain"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        List<GitWorktreeInfo> worktrees = [];
        string? path = null;
        string? head = null;
        string? branch = null;

        void Flush()
        {
            if (path is not null)
            {
                string fullPath = Path.GetFullPath(path);
                if (IsDirectChild(worktreesDirectory, fullPath))
                {
                    worktrees.Add(new GitWorktreeInfo(
                        Path.GetFileName(fullPath),
                        fullPath,
                        branch,
                        head));
                }
            }

            path = null;
            head = null;
            branch = null;
        }

        foreach (string rawLine in result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                string reference = line["branch ".Length..];
                branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? reference["refs/heads/".Length..]
                    : reference;
            }
        }

        Flush();
        return worktrees;
    }

    private async Task RemoveCoreAsync(
        string workspaceRoot,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string fullWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        string repositoryRoot = await GetRepositoryRootAsync(fullWorkspaceRoot, cancellationToken);
        string worktreeName = SanitizeName(name);
        string worktreePath = Path.Combine(GetWorktreesDirectory(fullWorkspaceRoot), worktreeName);

        IReadOnlyList<GitWorktreeInfo> worktrees = await ListCoreAsync(fullWorkspaceRoot, cancellationToken);
        GitWorktreeInfo? match = worktrees.FirstOrDefault(worktree =>
            string.Equals(worktree.Name, worktreeName, StringComparison.OrdinalIgnoreCase));
        if (match is null && !Directory.Exists(worktreePath))
        {
            throw new InvalidOperationException($"No StemCode worktree named '{worktreeName}'.");
        }

        await RemoveWorktreeAndBranchAsync(
            repositoryRoot,
            match?.WorktreePath ?? worktreePath,
            match?.Branch ?? BranchPrefix + worktreeName,
            cancellationToken);
    }

    internal static string SanitizeName(string name)
    {
        StringBuilder builder = new(Math.Min(name.Length, MaxNameLength));
        foreach (char character in name.Trim())
        {
            if (builder.Length >= MaxNameLength)
            {
                break;
            }

            char lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')
            {
                builder.Append(lower);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string sanitized = builder.ToString().Trim('-', '.');
        if (sanitized.Length == 0)
        {
            throw new ArgumentException("Worktree name must contain letters or digits.", nameof(name));
        }

        return sanitized;
    }

    internal static string GetWorktreesDirectory(string workspaceRoot)
    {
        return Path.Combine(
            Path.GetFullPath(workspaceRoot),
            WorktreesRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string CreateDefaultName()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"agent-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}");
    }

    private static void EnsureWorktreesDirectory(string worktreesDirectory)
    {
        Directory.CreateDirectory(worktreesDirectory);
        string gitIgnorePath = Path.Combine(worktreesDirectory, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            File.WriteAllText(
                gitIgnorePath,
                "# Managed by StemCode: isolated git worktrees for subagents and parallel sessions.\n*\n");
        }
    }

    private async Task RemoveWorktreeAndBranchAsync(
        string repositoryRoot,
        string worktreePath,
        string? branch,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult removeResult = await RunGitAsync(
            repositoryRoot,
            ["worktree", "remove", "--force", worktreePath],
            cancellationToken);
        if (removeResult.ExitCode != 0 && Directory.Exists(worktreePath))
        {
            try
            {
                Directory.Delete(worktreePath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _ = await RunGitAsync(repositoryRoot, ["worktree", "prune"], cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(branch) &&
            branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            _ = await RunGitAsync(repositoryRoot, ["branch", "-D", branch], cancellationToken);
        }
    }

    private async Task<string> GetRepositoryRootAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        ProcessExecutionResult result = await RunGitAsync(
            workspaceRoot,
            ["rev-parse", "--show-toplevel"],
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Worktree isolation needs a git repository, but '{workspaceRoot}' is not inside one.");
        }

        return Path.GetFullPath(result.StandardOutput.Trim());
    }

    private Task<ProcessExecutionResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        return _processRunner.RunAsync(
            new ProcessExecutionRequest(
                "git",
                arguments,
                WorkingDirectory: workingDirectory,
                MaxOutputCharacters: 200_000),
            cancellationToken);
    }

    private static IEnumerable<string> ParsePorcelainPaths(string output)
    {
        // Porcelain lines are "XY path"; X is often a space, so the lines must not be trimmed.
        foreach (string line in (output ?? string.Empty)
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length <= 3)
            {
                continue;
            }

            string path = line[3..];
            int renameArrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (renameArrow >= 0)
            {
                path = path[(renameArrow + 4)..];
            }

            yield return path.Trim('"');
        }
    }

    private static IEnumerable<string> SplitLines(string? output)
    {
        return (output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsDirectChild(string parentDirectory, string candidate)
    {
        string? candidateParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(candidate));
        return candidateParent is not null &&
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateParent)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.Select(static value => value?.Trim()).FirstOrDefault(static value => !string.IsNullOrEmpty(value))
            ?? "unknown error";
    }
}
