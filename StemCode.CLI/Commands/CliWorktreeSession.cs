using StemCode.Application.Abstractions;
using StemCode.Infrastructure.Git;
using StemCode.Infrastructure.Secrets;

namespace StemCode.CLI;

/// <summary>
/// Runs a whole CLI session inside a fresh git worktree (<c>--worktree</c>), so
/// several StemCode sessions can work on the same repository in parallel without
/// editing the same files. The worktree is removed on exit when nothing changed.
/// </summary>
internal static class CliWorktreeSession
{
    public static async Task<int> RunAsync(
        string worktreeName,
        Func<Task<int>> runSession)
    {
        ArgumentNullException.ThrowIfNull(worktreeName);
        ArgumentNullException.ThrowIfNull(runSession);

        IGitWorktreeService worktreeService = new GitWorktreeService(new ProcessRunner());
        string originalDirectory = Directory.GetCurrentDirectory();

        GitWorktreeHandle worktree;
        try
        {
            worktree = await worktreeService.CreateAsync(
                originalDirectory,
                string.IsNullOrWhiteSpace(worktreeName) ? null : worktreeName,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not start in a worktree: {exception.Message}");
            return ExitCodeMapper.UsageError;
        }

        Console.Error.WriteLine($"Working in worktree {worktree.Name} on branch {worktree.Branch}");
        Console.Error.WriteLine($"  {worktree.WorkspaceRoot}");

        int exitCode;
        Directory.SetCurrentDirectory(worktree.WorkspaceRoot);
        try
        {
            exitCode = await runSession();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            await ReportOutcomeAsync(worktreeService, worktree);
        }

        return exitCode;
    }

    private static async Task ReportOutcomeAsync(
        IGitWorktreeService worktreeService,
        GitWorktreeHandle worktree)
    {
        try
        {
            GitWorktreeOutcome outcome = await worktreeService.FinishAsync(worktree, CancellationToken.None);
            if (!outcome.Kept)
            {
                Console.Error.WriteLine($"Worktree {worktree.Name} had no changes and was removed.");
                return;
            }

            Console.Error.WriteLine(
                $"Worktree {worktree.Name} kept with {outcome.ChangedFiles.Count} changed file(s) and {outcome.CommitCount} commit(s).");
            Console.Error.WriteLine($"  Path:   {worktree.WorktreePath}");
            Console.Error.WriteLine($"  Branch: {worktree.Branch}");
            Console.Error.WriteLine($"  Merge with `git merge {worktree.Branch}`, then run /worktree remove {worktree.Name}.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            Console.Error.WriteLine($"Could not check worktree {worktree.Name}: {exception.Message}");
        }
    }
}
