using System.Diagnostics;
using System.Text;

namespace StemCode.Desktop.Services;

/// <summary>Produces unified diffs for workspace files using the local git executable.</summary>
public static class GitDiffService
{
    private const int MaxSyntheticLines = 2_000;

    public static async Task<string> GetFileDiffAsync(string workspaceRoot, string absolutePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(absolutePath) && !await IsTrackedAsync(workspaceRoot, absolutePath, cancellationToken))
        {
            return $"{Path.GetFileName(absolutePath)} no longer exists.";
        }

        GitResult inside = await RunGitAsync(workspaceRoot, ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0)
        {
            return SyntheticAddedDiff(absolutePath, "Not a git repository — showing the current file.");
        }

        GitResult diff = await RunGitAsync(workspaceRoot, ["diff", "--no-color", "--no-ext-diff", "HEAD", "--", absolutePath], cancellationToken);
        if (diff.ExitCode == 0 && !string.IsNullOrWhiteSpace(diff.Output))
        {
            return diff.Output;
        }

        if (!await IsTrackedAsync(workspaceRoot, absolutePath, cancellationToken))
        {
            return SyntheticAddedDiff(absolutePath, "New untracked file.");
        }

        // Repos with no commits yet have no HEAD; fall back to the index.
        GitResult unstaged = await RunGitAsync(workspaceRoot, ["diff", "--no-color", "--no-ext-diff", "--", absolutePath], cancellationToken);
        return string.IsNullOrWhiteSpace(unstaged.Output)
            ? "No differences from the last commit."
            : unstaged.Output;
    }

    public static async Task<string> GetWorkspaceDiffAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        GitResult diff = await RunGitAsync(workspaceRoot, ["diff", "--no-color", "--no-ext-diff", "HEAD"], cancellationToken);
        if (diff.ExitCode != 0)
        {
            return string.IsNullOrWhiteSpace(diff.Error) ? "git diff failed." : diff.Error.Trim();
        }

        return string.IsNullOrWhiteSpace(diff.Output) ? "Working tree matches HEAD." : diff.Output;
    }

    private static async Task<bool> IsTrackedAsync(string workspaceRoot, string absolutePath, CancellationToken cancellationToken)
    {
        GitResult result = await RunGitAsync(workspaceRoot, ["ls-files", "--error-unmatch", "--", absolutePath], cancellationToken);
        return result.ExitCode == 0;
    }

    private static string SyntheticAddedDiff(string absolutePath, string note)
    {
        StringBuilder builder = new();
        builder.Append("@@ ").Append(note).AppendLine(" @@");
        try
        {
            int count = 0;
            foreach (string line in File.ReadLines(absolutePath))
            {
                if (++count > MaxSyntheticLines)
                {
                    builder.AppendLine("    ... file truncated");
                    break;
                }

                builder.Append('+').AppendLine(line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            builder.AppendLine(exception.Message);
        }

        return builder.ToString();
    }

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new GitResult(process.ExitCode, await output, await error);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new GitResult(-1, string.Empty, "git is not available: " + exception.Message);
        }
    }

    private sealed record GitResult(int ExitCode, string Output, string Error);
}
