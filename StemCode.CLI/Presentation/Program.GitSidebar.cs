using StemCode.Application.Models;
using StemCode.Application.UI;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Diagnostics;

namespace StemCode.CLI;

public enum GitSidebarLineKind
{
    Text,
    Repo,
    Branch,
    Commit,
    LoadMoreCommits,
    StagedFile,
    ChangedFile
}

// One rendered row of the git sidebar. Actionable rows can carry either a file path
// or commit metadata depending on their kind.
public sealed record GitSidebarLine(
    string Markup,
    string Plain,
    string? FilePath,
    GitSidebarLineKind Kind = GitSidebarLineKind.Text,
    string? RelativePath = null,
    string? StatusCode = null,
    string? CommitHash = null,
    string? CommitMessage = null);

public static partial class Program
{
    private const int GitSidebarMinWindowWidth = 30;
    private const int GitSidebarCommitPageSize = 10;
    private const string GitSidebarBackgroundMarkupStyle = "white on #1c1c1c";
    private const int GitSidebarHorizontalPadding = 1;
    private const int GitSidebarVerticalPadding = 1;
    private const int GitSidebarCommitMessageSuggestionMaxLength = 225;
    private const string GitSidebarCommitCoAuthorTrailer = "Co-authored-by: StemCodeAi <317135024+stemcodeai[bot]@users.noreply.github.com>";

    private static bool TryGetGitSidebarWidth(AppState state, int windowWidth, out int width)
    {
        width = 0;
        if (!state.IsGitSidebarVisible || windowWidth < GitSidebarMinWindowWidth)
        {
            return false;
        }

        width = Math.Clamp(windowWidth / 5, 15, 30);
        return true;
    }

    private static void ToggleGitSidebar(AppState state)
    {
        state.IsGitSidebarVisible = !state.IsGitSidebarVisible;
        if (state.IsGitSidebarVisible)
        {
            InvalidateGitSidebar(state);
        }
    }

    private static void InvalidateGitSidebar(AppState state)
    {
        state.GitSidebarCache = null;
        state.GitSidebarCacheTime = DateTimeOffset.MinValue;
    }

    private static IRenderable BuildGitSidebarPanel(AppState state, int windowHeight)
    {
        IReadOnlyList<GitSidebarLine> lines = GetGitSidebarLines(state);
        int viewportHeight = Math.Max(1, windowHeight - 1 - (GitSidebarVerticalPadding * 2));

        int maxScroll = Math.Max(0, lines.Count - viewportHeight);
        state.GitSidebarScrollOffset = Math.Clamp(state.GitSidebarScrollOffset, 0, maxScroll);
        state.GitSidebarTotalLineCount = lines.Count;
        state.GitSidebarViewportHeight = viewportHeight;

        EnsureGitSidebarSelection(state, lines);

        int start = state.GitSidebarScrollOffset;
        List<string> markup = [];
        GitSidebarLine[] visibleLines = new GitSidebarLine[viewportHeight];
        for (int index = 0; index < viewportHeight; index++)
        {
            int sourceIndex = start + index;
            if (sourceIndex < lines.Count)
            {
                GitSidebarLine line = lines[sourceIndex];
                markup.Add(sourceIndex == state.GitSidebarSelectedIndex
                    ? HighlightGitSidebarMarkup(line)
                    : line.Markup);
                visibleLines[index] = line;
            }
            else
            {
                markup.Add(string.Empty);
                visibleLines[index] = new GitSidebarLine(string.Empty, string.Empty, null);
            }
        }

        state.VisibleGitSidebarLines = visibleLines;

        string scrollHint = lines.Count > viewportHeight
            ? $" [grey]{start + 1}-{Math.Min(lines.Count, start + viewportHeight)}/{lines.Count}[/]"
            : string.Empty;

        string paddingMarkup = ApplyGitSidebarBackground(string.Empty, state.GitSidebarWidth, GitSidebarHorizontalPadding);
        string titleMarkup = ApplyGitSidebarBackground(
            SafeHeaderMarkup($"[bold]Git[/] [grey](F7)[/]{scrollHint}"),
            state.GitSidebarWidth,
            GitSidebarHorizontalPadding);
        string contentMarkup = ApplyGitSidebarBackground(string.Join('\n', markup), state.GitSidebarWidth, GitSidebarHorizontalPadding);
        return new Markup($"{paddingMarkup}\n{titleMarkup}\n{contentMarkup}\n{paddingMarkup}");
    }

    private static string ApplyGitSidebarBackground(string markup, int contentWidth, int horizontalPadding = 0)
    {
        string[] lines = (markup ?? string.Empty).Split('\n');
        int innerWidth = Math.Max(0, contentWidth - (horizontalPadding * 2));
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            int visibleLength = Markup.Remove(line).Length;
            int trailingSpaces = Math.Max(0, innerWidth - visibleLength);
            lines[index] = $"[{GitSidebarBackgroundMarkupStyle}]{new string(' ', horizontalPadding)}{line}{new string(' ', trailingSpaces)}{new string(' ', horizontalPadding)}[/]";
        }

        return string.Join('\n', lines);
    }

    private static string HighlightGitSidebarMarkup(GitSidebarLine line)
    {
        string text = string.IsNullOrEmpty(line.Plain) ? " " : line.Plain;
        return $"[black on grey70]{Markup.Escape(text)}[/]";
    }

    private static void ScrollGitSidebar(AppState state, int delta)
    {
        int maxScroll = Math.Max(0, state.GitSidebarTotalLineCount - state.GitSidebarViewportHeight);
        state.GitSidebarScrollOffset = Math.Clamp(state.GitSidebarScrollOffset + delta, 0, maxScroll);
    }

    private static bool TryHandleGitSidebarScrollKey(AppState state, ConsoleKeyInfo key)
    {
        if (!key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            return false;
        }

        int page = Math.Max(1, state.GitSidebarViewportHeight - 1);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                ScrollGitSidebar(state, -MouseWheelScrollLineCount);
                return true;
            case ConsoleKey.DownArrow:
                ScrollGitSidebar(state, MouseWheelScrollLineCount);
                return true;
            case ConsoleKey.PageUp:
                ScrollGitSidebar(state, -page);
                return true;
            case ConsoleKey.PageDown:
                ScrollGitSidebar(state, page);
                return true;
            default:
                return false;
        }
    }

    private static bool TryHandleGitSidebarKey(AppState state, ConsoleKeyInfo key)
    {
        if (state.ActiveModal is not null || state.IsReaderViewActive || state.IsCopyModeActive)
        {
            return false;
        }

        IReadOnlyList<GitSidebarLine> lines = GetGitSidebarLines(state);
        EnsureGitSidebarSelection(state, lines);

        int pageSize = Math.Max(1, state.GitSidebarViewportHeight - 1);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveGitSidebarSelection(state, lines, -1);
                return true;
            case ConsoleKey.DownArrow:
                MoveGitSidebarSelection(state, lines, 1);
                return true;
            case ConsoleKey.Home:
                MoveGitSidebarSelectionToEdge(state, lines, forward: true);
                return true;
            case ConsoleKey.End:
                MoveGitSidebarSelectionToEdge(state, lines, forward: false);
                return true;
            case ConsoleKey.PageUp:
                MoveGitSidebarSelectionByPage(state, lines, -pageSize);
                return true;
            case ConsoleKey.PageDown:
                MoveGitSidebarSelectionByPage(state, lines, pageSize);
                return true;
            case ConsoleKey.Enter:
                ActivateGitSidebarSelection(state);
                return true;
            case ConsoleKey.A when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                OpenGitSidebarActionMenu(state);
                return true;
            case ConsoleKey.S when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                ToggleGitSidebarStageSelection(state);
                return true;
            case ConsoleKey.O when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                RunGitPushFromSidebar(state);
                return true;
            case ConsoleKey.P when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                RunGitPullFromSidebar(state);
                return true;
            case ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                PromptDiscardGitSidebarSelection(state);
                return true;
            case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                PromptCommitFromGitSidebar(state);
                return true;
            case ConsoleKey.B when key.Modifiers.HasFlag(ConsoleModifiers.Alt):
                PromptBranchActionFromGitSidebar(state);
                return true;
            default:
                return false;
        }
    }

    private static IReadOnlyList<GitSidebarLine> GetGitSidebarLines(AppState state)
    {
        if (state.GitSidebarCache is { } cached &&
            DateTimeOffset.UtcNow - state.GitSidebarCacheTime < TimeSpan.FromSeconds(2))
        {
            return cached;
        }

        IReadOnlyList<GitSidebarLine> lines = BuildGitSidebarLines(state);
        state.GitSidebarCache = lines;
        state.GitSidebarCacheTime = DateTimeOffset.UtcNow;
        return lines;
    }

    private static IReadOnlyList<GitSidebarLine> BuildGitSidebarLines(AppState state)
    {
        int contentWidth = Math.Max(8, state.GitSidebarWidth - (GitSidebarHorizontalPadding * 2));
        string root = state.RootDirectory;
        List<GitSidebarLine> lines = [];

        string? gitDir = FindGitDirectory(root);
        if (gitDir is null)
        {
            lines.Add(PlainSidebarLine("Not a git repository.", contentWidth, "grey"));
            return lines;
        }

        string repoRoot = Directory.GetParent(gitDir.TrimEnd('\\', '/'))?.FullName ?? root;
        string? branch = GetGitBranchName(root);

        lines.Add(BuildRepoSidebarLine(repoRoot, contentWidth));
        lines.Add(BuildBranchSidebarLine(branch, contentWidth));

        AddQueuedPromptLines(lines, state, contentWidth);

        (List<(string Code, string Rel)> staged, List<(string Code, string Rel)> changes) = ReadGitStatus(root);
        AddFileLines(lines, "Staged", staged, repoRoot, contentWidth, GitSidebarLineKind.StagedFile);
        AddFileLines(lines, "Changes", changes, repoRoot, contentWidth, GitSidebarLineKind.ChangedFile);

        lines.Add(new GitSidebarLine(string.Empty, string.Empty, null));
        lines.Add(SectionSidebarLine("Recent commits", contentWidth));
        AddCommitLines(lines, root, contentWidth, Math.Max(GitSidebarCommitPageSize, state.GitSidebarCommitDisplayCount));

        return lines;
    }

    private static void AddQueuedPromptLines(List<GitSidebarLine> lines, AppState state, int contentWidth)
    {
        if (state.PendingSubmissions.Count == 0)
        {
            return;
        }

        lines.Add(new GitSidebarLine(string.Empty, string.Empty, null));
        lines.Add(SectionSidebarLine($"Queued ({state.PendingSubmissions.Count})", contentWidth));

        foreach (PendingSubmission submission in state.PendingSubmissions)
        {
            string text = TruncateFromRight(DescribePendingSubmission(submission), Math.Max(0, contentWidth - 2));
            lines.Add(new GitSidebarLine(
                $" [aqua]*[/] [grey]{Markup.Escape(text)}[/]",
                $" * {text}",
                null));
        }
    }

    private static void AddCommitLines(List<GitSidebarLine> lines, string root, int contentWidth, int commitLimit)
    {
        HashSet<string> localOnlyCommits = GetLocalOnlyCommitHashes(root);
        int fetchCount = Math.Max(1, commitLimit) + 1;
        string? log = RunGit(root, "log", $"-{fetchCount}", "--pretty=format:%h%x09%H%x09%s");
        if (string.IsNullOrEmpty(log))
        {
            lines.Add(PlainSidebarLine("  (none)", contentWidth, "grey"));
            return;
        }

        string[] rawLines = log
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool hasMore = rawLines.Length > commitLimit;

        foreach (string raw in rawLines.Take(commitLimit))
        {
            string[] parts = raw.Split('\t', 3);
            string shortHash = parts.Length > 0 ? parts[0] : raw;
            string fullHash = parts.Length > 1 ? parts[1] : shortHash;
            string message = parts.Length > 2 ? parts[2] : string.Empty;
            string messageTrunc = TruncateFromRight(message, Math.Max(0, contentWidth - shortHash.Length - 2));
            string plain = $" {shortHash} {messageTrunc}";
            string hashColor = localOnlyCommits.Contains(fullHash) ? "blue" : "yellow";
            string markup = $" [{hashColor}]{Markup.Escape(shortHash)}[/] [underline][grey]{Markup.Escape(messageTrunc)}[/][/]";
            lines.Add(new GitSidebarLine(
                markup,
                plain,
                null,
                GitSidebarLineKind.Commit,
                CommitHash: fullHash,
                CommitMessage: message));
        }

        if (hasMore)
        {
            string text = TruncateFromRight("Load more commits...", contentWidth);
            lines.Add(new GitSidebarLine(
                $"[aqua]{Markup.Escape(text)}[/]",
                text,
                null,
                GitSidebarLineKind.LoadMoreCommits));
        }
    }

    private static GitSidebarLine BuildBranchSidebarLine(string? branch, int contentWidth)
    {
        string branchName = branch ?? "(detached)";
        string plain = TruncateFromRight($"branch {branchName}", contentWidth);
        string branchNameTrunc = TruncateFromRight(branchName, Math.Max(0, contentWidth - "branch ".Length));
        string markup = $"[bold][grey]branch[/] [green]{Markup.Escape(branchNameTrunc)}[/][/]";
        return new GitSidebarLine(markup, plain, null, GitSidebarLineKind.Branch);
    }

    private static GitSidebarLine BuildRepoSidebarLine(string repoRoot, int contentWidth)
    {
        string repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(repoName))
        {
            repoName = repoRoot;
        }

        string plain = TruncateFromRight($"repo {repoName}", contentWidth);
        string repoNameTrunc = TruncateFromRight(repoName, Math.Max(0, contentWidth - "repo ".Length));
        string markup = $"[bold][grey]repo[/] [aqua]{Markup.Escape(repoNameTrunc)}[/][/]";
        return new GitSidebarLine(markup, plain, repoRoot, GitSidebarLineKind.Repo);
    }

    private static HashSet<string> GetLocalOnlyCommitHashes(string root)
    {
        string? output = RunGit(root, "log", "HEAD", "--not", "--remotes", "--pretty=format:%H");
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AddFileLines(
        List<GitSidebarLine> lines,
        string title,
        List<(string Code, string Rel)> items,
        string repoRoot,
        int contentWidth,
        GitSidebarLineKind kind)
    {
        lines.Add(new GitSidebarLine(string.Empty, string.Empty, null));
        lines.Add(SectionSidebarLine($"{title} ({items.Count})", contentWidth));

        if (items.Count == 0)
        {
            lines.Add(PlainSidebarLine("  (none)", contentWidth, "grey"));
            return;
        }

        foreach ((string code, string rel) in items)
        {
            string color = code switch
            {
                "?" => "grey",
                "D" => "red",
                "A" => "green",
                "M" => "yellow",
                _ => "aqua"
            };
            string displayName = BuildGitSidebarFileDisplayText(rel);
            string nameTrunc = TruncateFromRight(displayName, Math.Max(0, contentWidth - 3));
            string plain = $" {code} {nameTrunc}";
            string markup = $" [{color}]{Markup.Escape(code)}[/] [underline]{Markup.Escape(nameTrunc)}[/]";
            string full = Path.GetFullPath(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            lines.Add(new GitSidebarLine(markup, plain, full, kind, rel, code));
        }
    }

    private static (List<(string Code, string Rel)> Staged, List<(string Code, string Rel)> Changes) ReadGitStatus(string root)
    {
        List<(string, string)> staged = [];
        List<(string, string)> changes = [];

        string? status = RunGit(root, "status", "--porcelain");
        if (string.IsNullOrEmpty(status))
        {
            return (staged, changes);
        }

        foreach (string line in status.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length < 4)
            {
                continue;
            }

            char index = line[0];
            char worktree = line[1];
            string path = line[3..];

            int arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                path = path[(arrow + 4)..];
            }

            path = UnquoteGitPath(path);

            if (index != ' ' && index != '?')
            {
                staged.Add((index.ToString(), path));
            }

            if (worktree == '?')
            {
                changes.Add(("?", path));
            }
            else if (worktree != ' ')
            {
                changes.Add((worktree.ToString(), path));
            }
        }

        return (staged, changes);
    }

    private static GitSidebarLine SectionSidebarLine(string text, int contentWidth)
    {
        string trunc = TruncateFromRight(text, contentWidth);
        return new GitSidebarLine($"[bold]{Markup.Escape(trunc)}[/]", trunc, null);
    }

    private static string SectionSidebarMarkup(string text, int contentWidth)
    {
        string trunc = TruncateFromRight(text, contentWidth);
        return $"[bold]{Markup.Escape(trunc)}[/]";
    }

    private static GitSidebarLine PlainSidebarLine(string text, int contentWidth, string color)
    {
        string trunc = TruncateFromRight(text, contentWidth);
        return new GitSidebarLine($"[{color}]{Markup.Escape(trunc)}[/]", trunc, null);
    }

    private static string UnquoteGitPath(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            return path[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return path;
    }

    private static string BuildGitSidebarFileDisplayText(string relativePath)
    {
        string normalizedPath = relativePath.Replace('\\', '/');
        string fileName = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(fileName) ||
            string.Equals(fileName, normalizedPath, StringComparison.Ordinal))
        {
            return normalizedPath;
        }

        return $"{fileName} ({normalizedPath})";
    }

    private static void EnsureGitSidebarSelection(AppState state, IReadOnlyList<GitSidebarLine> lines)
    {
        if (lines.Count == 0)
        {
            state.GitSidebarSelectedIndex = -1;
            return;
        }

        if (IsGitSidebarActionable(lines, state.GitSidebarSelectedIndex))
        {
            EnsureGitSidebarSelectionVisible(state);
            return;
        }

        state.GitSidebarSelectedIndex = FindNextGitSidebarActionableIndex(lines, 0, forward: true);
        EnsureGitSidebarSelectionVisible(state);
    }

    private static bool IsGitSidebarActionable(IReadOnlyList<GitSidebarLine> lines, int index)
    {
        return index >= 0 &&
            index < lines.Count &&
            lines[index].Kind is GitSidebarLineKind.Repo or GitSidebarLineKind.Branch or GitSidebarLineKind.Commit or GitSidebarLineKind.LoadMoreCommits or GitSidebarLineKind.StagedFile or GitSidebarLineKind.ChangedFile;
    }

    private static int FindNextGitSidebarActionableIndex(
        IReadOnlyList<GitSidebarLine> lines,
        int startIndex,
        bool forward)
    {
        if (lines.Count == 0)
        {
            return -1;
        }

        int index = Math.Clamp(startIndex, 0, lines.Count - 1);
        int step = forward ? 1 : -1;

        while (index >= 0 && index < lines.Count)
        {
            if (IsGitSidebarActionable(lines, index))
            {
                return index;
            }

            index += step;
        }

        return -1;
    }

    private static void MoveGitSidebarSelection(AppState state, IReadOnlyList<GitSidebarLine> lines, int direction)
    {
        if (lines.Count == 0)
        {
            return;
        }

        int current = Math.Clamp(state.GitSidebarSelectedIndex, 0, lines.Count - 1);
        int next = FindNextGitSidebarActionableIndex(lines, current + direction, direction > 0);
        if (next >= 0)
        {
            state.GitSidebarSelectedIndex = next;
            EnsureGitSidebarSelectionVisible(state);
        }
    }

    private static void MoveGitSidebarSelectionToEdge(AppState state, IReadOnlyList<GitSidebarLine> lines, bool forward)
    {
        int next = FindNextGitSidebarActionableIndex(lines, forward ? 0 : lines.Count - 1, forward);
        if (next >= 0)
        {
            state.GitSidebarSelectedIndex = next;
            EnsureGitSidebarSelectionVisible(state);
        }
    }

    private static void MoveGitSidebarSelectionByPage(AppState state, IReadOnlyList<GitSidebarLine> lines, int delta)
    {
        if (lines.Count == 0)
        {
            return;
        }

        int current = Math.Clamp(state.GitSidebarSelectedIndex, 0, lines.Count - 1);
        int target = Math.Clamp(current + delta, 0, lines.Count - 1);
        int next = FindNextGitSidebarActionableIndex(lines, target, delta >= 0);
        if (next < 0)
        {
            next = FindNextGitSidebarActionableIndex(lines, target, delta < 0);
        }

        if (next >= 0)
        {
            state.GitSidebarSelectedIndex = next;
            EnsureGitSidebarSelectionVisible(state);
        }
    }

    private static void EnsureGitSidebarSelectionVisible(AppState state)
    {
        if (state.GitSidebarSelectedIndex < 0)
        {
            return;
        }

        if (state.GitSidebarSelectedIndex < state.GitSidebarScrollOffset)
        {
            state.GitSidebarScrollOffset = state.GitSidebarSelectedIndex;
            return;
        }

        int viewportBottom = state.GitSidebarScrollOffset + Math.Max(1, state.GitSidebarViewportHeight) - 1;
        if (state.GitSidebarSelectedIndex > viewportBottom)
        {
            state.GitSidebarScrollOffset = Math.Max(0, state.GitSidebarSelectedIndex - Math.Max(1, state.GitSidebarViewportHeight) + 1);
        }
    }

    private static GitSidebarLine? GetSelectedGitSidebarLine(AppState state)
    {
        IReadOnlyList<GitSidebarLine> lines = GetGitSidebarLines(state);
        EnsureGitSidebarSelection(state, lines);

        return IsGitSidebarActionable(lines, state.GitSidebarSelectedIndex)
            ? lines[state.GitSidebarSelectedIndex]
            : null;
    }

    private static void ActivateGitSidebarSelection(AppState state)
    {
        GitSidebarLine? selected = GetSelectedGitSidebarLine(state);
        if (selected is null)
        {
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Repo)
        {
            PromptRepoActionMenu(state);
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Branch)
        {
            PromptBranchActionFromGitSidebar(state);
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Commit)
        {
            PromptCommitActionFromGitSidebar(state, selected);
            return;
        }

        if (selected.Kind == GitSidebarLineKind.LoadMoreCommits)
        {
            LoadMoreGitSidebarCommits(state);
            return;
        }

        if (selected.FilePath is string path)
        {
            if (File.Exists(path))
            {
                OpenFileInEditor(state, path);
                return;
            }

            OpenGitSidebarFileChanges(state, selected);
        }
    }

    private static void OpenGitSidebarActionMenu(AppState state)
    {
        GitSidebarLine? selected = GetSelectedGitSidebarLine(state);
        if (selected is null)
        {
            return;
        }

        switch (selected.Kind)
        {
            case GitSidebarLineKind.Repo:
                PromptRepoActionMenu(state);
                return;
            case GitSidebarLineKind.Branch:
                PromptBranchActionFromGitSidebar(state);
                return;
            case GitSidebarLineKind.Commit:
                PromptCommitActionFromGitSidebar(state, selected);
                return;
            case GitSidebarLineKind.LoadMoreCommits:
                LoadMoreGitSidebarCommits(state);
                return;
            case GitSidebarLineKind.StagedFile:
            case GitSidebarLineKind.ChangedFile:
                PromptFileActionFromGitSidebar(state, selected);
                return;
        }
    }

    private static void ToggleGitSidebarStageSelection(AppState state)
    {
        GitSidebarLine? selected = GetSelectedGitSidebarLine(state);
        if (selected is null)
        {
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Repo)
        {
            PromptRepoActionMenu(state);
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Branch)
        {
            PromptBranchActionFromGitSidebar(state);
            return;
        }

        if (selected.Kind == GitSidebarLineKind.Commit)
        {
            PromptCommitActionFromGitSidebar(state, selected);
            return;
        }

        if (string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            return;
        }

        bool success = selected.Kind switch
        {
            GitSidebarLineKind.ChangedFile => TryRunGitCommand(
                state,
                $"Staged {selected.RelativePath}.",
                "Failed to stage file",
                "add",
                "--",
                selected.RelativePath),
            GitSidebarLineKind.StagedFile => TryRunGitCommand(
                state,
                $"Unstaged {selected.RelativePath}.",
                "Failed to unstage file",
                "restore",
                "--staged",
                "--",
                selected.RelativePath),
            _ => false
        };

        if (success)
        {
            InvalidateGitSidebar(state);
        }
    }

    private static void PromptRepoActionMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository actions",
                [
                    new SelectionPromptOption<string>("Sync", "sync", "Pull, push, fetch, publish, and remote-targeted sync actions."),
                    new SelectionPromptOption<string>("Changes", "changes", "Stage, unstage, discard, and undo the last commit."),
                    new SelectionPromptOption<string>("Stash", "stash", "Create, apply, pop, drop, and inspect stashes."),
                    new SelectionPromptOption<string>("Branches", "branches", "Merge, rebase, create, rename, and delete branches."),
                    new SelectionPromptOption<string>("Remotes", "remotes", "Add or delete remotes."),
                    new SelectionPromptOption<string>("Tags", "tags", "Create, delete, and publish tags.")
                ],
                "Choose a repository action group. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "sync":
                        PromptRepoSyncMenu(state);
                        break;
                    case "changes":
                        PromptRepoChangesMenu(state);
                        break;
                    case "stash":
                        PromptRepoStashMenu(state);
                        break;
                    case "branches":
                        PromptRepoBranchMenu(state);
                        break;
                    case "remotes":
                        PromptRepoRemoteMenu(state);
                        break;
                    case "tags":
                        PromptRepoTagMenu(state);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Repository action cancelled."));
    }

    private static void PromptRepoSyncMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository sync",
                [
                    new SelectionPromptOption<string>("Pull", "pull", "Run git pull for the current branch."),
                    new SelectionPromptOption<string>("Pull (rebase)", "pull-rebase", "Run git pull --rebase for the current branch."),
                    new SelectionPromptOption<string>("Pull from...", "pull-from", "Choose a remote and branch to pull from."),
                    new SelectionPromptOption<string>("Push", "push", "Run git push for the current branch."),
                    new SelectionPromptOption<string>("Push to...", "push-to", "Choose a remote and branch to push to."),
                    new SelectionPromptOption<string>("Fetch", "fetch", "Fetch from the default remotes."),
                    new SelectionPromptOption<string>("Fetch (prune)", "fetch-prune", "Fetch and prune deleted remote refs."),
                    new SelectionPromptOption<string>("Fetch all remotes", "fetch-all", "Fetch from every configured remote."),
                    new SelectionPromptOption<string>("Publish branch", "publish", "Push the current branch to origin and set upstream."),
                    new SelectionPromptOption<string>("Push tags", "push-tags", "Push every local tag to the default remote.")
                ],
                "Choose a sync action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "pull":
                        RunGitPullFromSidebar(state);
                        break;
                    case "pull-rebase":
                        if (TryRunGitCommand(state, "Git pull --rebase completed.", "Failed to pull with rebase", "pull", "--rebase"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "pull-from":
                        PromptPullFromRemote(state);
                        break;
                    case "push":
                        RunGitPushFromSidebar(state);
                        break;
                    case "push-to":
                        PromptPushToRemote(state);
                        break;
                    case "fetch":
                        if (TryRunGitCommand(state, "Git fetch completed.", "Failed to fetch", "fetch"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "fetch-prune":
                        if (TryRunGitCommand(state, "Git fetch --prune completed.", "Failed to fetch with prune", "fetch", "--prune"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "fetch-all":
                        if (TryRunGitCommand(state, "Fetched all remotes.", "Failed to fetch all remotes", "fetch", "--all", "--prune"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "publish":
                        PromptPublishBranch(state);
                        break;
                    case "push-tags":
                        if (TryRunGitCommand(state, "Pushed all tags.", "Failed to push tags", "push", "--tags"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Sync action cancelled."));
    }

    private static void PromptRepoChangesMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository changes",
                [
                    new SelectionPromptOption<string>("Stage all changes", "stage-all", "Stage tracked, deleted, and untracked files."),
                    new SelectionPromptOption<string>("Unstage all changes", "unstage-all", "Remove every staged change from the index."),
                    new SelectionPromptOption<string>("Discard all changes", "discard-all", "Restore tracked files and delete untracked files."),
                    new SelectionPromptOption<string>("Undo last commit", "undo-last-commit", "Move HEAD back one commit and keep the changes locally.")
                ],
                "Choose a changes action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "stage-all":
                        if (TryRunGitCommand(state, "Staged all changes.", "Failed to stage all changes", "add", "-A"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "unstage-all":
                        if (TryRunGitCommand(state, "Unstaged all changes.", "Failed to unstage all changes", "restore", "--staged", "."))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                    case "discard-all":
                        PromptDiscardAllGitChanges(state);
                        break;
                    case "undo-last-commit":
                        PromptUndoLastCommit(state);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Changes action cancelled."));
    }

    private static void PromptRepoStashMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository stash",
                [
                    new SelectionPromptOption<string>("Stash", "stash", "Create a stash from tracked changes."),
                    new SelectionPromptOption<string>("Stash (include untracked)", "stash-untracked", "Create a stash and include untracked files."),
                    new SelectionPromptOption<string>("Stash staged", "stash-staged", "Create a stash from staged changes only."),
                    new SelectionPromptOption<string>("Apply last stash", "apply-last", "Apply stash@{0} without dropping it."),
                    new SelectionPromptOption<string>("Apply stash...", "apply", "Choose a stash entry to apply."),
                    new SelectionPromptOption<string>("Pop last stash", "pop-last", "Apply stash@{0} and drop it."),
                    new SelectionPromptOption<string>("Pop stash...", "pop", "Choose a stash entry to pop."),
                    new SelectionPromptOption<string>("Drop stash...", "drop", "Choose a stash entry to delete."),
                    new SelectionPromptOption<string>("Drop all stashes", "drop-all", "Delete every stash entry."),
                    new SelectionPromptOption<string>("View stash...", "view", "Open a stash patch inside the reader view.")
                ],
                "Choose a stash action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "stash":
                        PromptCreateStash(state, includeUntracked: false, stagedOnly: false);
                        break;
                    case "stash-untracked":
                        PromptCreateStash(state, includeUntracked: true, stagedOnly: false);
                        break;
                    case "stash-staged":
                        PromptCreateStash(state, includeUntracked: false, stagedOnly: true);
                        break;
                    case "apply-last":
                        RunGitStashAction(state, "apply", "stash@{0}", "Applied the latest stash.", "Failed to apply the latest stash");
                        break;
                    case "apply":
                        PromptSelectStash(state, "Apply stash", "Choose a stash entry to apply. Esc cancels.", stash => RunGitStashAction(state, "apply", stash.Ref, $"Applied {stash.Ref}.", "Failed to apply stash"), "No stashes found.", "Apply stash cancelled.");
                        break;
                    case "pop-last":
                        RunGitStashAction(state, "pop", "stash@{0}", "Popped the latest stash.", "Failed to pop the latest stash");
                        break;
                    case "pop":
                        PromptSelectStash(state, "Pop stash", "Choose a stash entry to pop. Esc cancels.", stash => RunGitStashAction(state, "pop", stash.Ref, $"Popped {stash.Ref}.", "Failed to pop stash"), "No stashes found.", "Pop stash cancelled.");
                        break;
                    case "drop":
                        PromptSelectStash(state, "Drop stash", "Choose a stash entry to delete. Esc cancels.", stash => PromptDropSelectedStash(state, stash), "No stashes found.", "Drop stash cancelled.");
                        break;
                    case "drop-all":
                        PromptDropAllStashes(state);
                        break;
                    case "view":
                        PromptSelectStash(state, "View stash", "Choose a stash entry to inspect. Esc cancels.", stash => OpenGitStashInReaderView(state, stash), "No stashes found.", "View stash cancelled.");
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Stash action cancelled."));
    }

    private static void PromptRepoBranchMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository branches",
                [
                    new SelectionPromptOption<string>("Merge branch...", "merge", "Merge another local branch into the current branch."),
                    new SelectionPromptOption<string>("Rebase branch...", "rebase", "Rebase the current branch onto another local branch."),
                    new SelectionPromptOption<string>("Create branch", "create", "Create and switch to a new branch from HEAD."),
                    new SelectionPromptOption<string>("Create branch from...", "create-from", "Create and switch to a new branch from a chosen ref."),
                    new SelectionPromptOption<string>("Rename current branch", "rename", "Rename the current local branch."),
                    new SelectionPromptOption<string>("Delete branch...", "delete", "Delete a local branch."),
                    new SelectionPromptOption<string>("Delete remote branch...", "delete-remote", "Delete a branch from a remote."),
                    new SelectionPromptOption<string>("Publish branch", "publish", "Push the current branch and set upstream.")
                ],
                "Choose a branch action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "merge":
                        PromptMergeBranch(state);
                        break;
                    case "rebase":
                        PromptRebaseOntoBranch(state);
                        break;
                    case "create":
                        PromptCreateBranchFromGitSidebar(
                            state,
                            startPoint: null,
                            "New branch name",
                            "Create and switch to a new git branch from HEAD. Enter submits, Esc cancels.");
                        break;
                    case "create-from":
                        PromptCreateBranchFromRef(state);
                        break;
                    case "rename":
                        PromptRenameCurrentBranch(state);
                        break;
                    case "delete":
                        PromptDeleteLocalBranch(state);
                        break;
                    case "delete-remote":
                        PromptDeleteRemoteBranch(state);
                        break;
                    case "publish":
                        PromptPublishBranch(state);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Branch action cancelled."));
    }

    private static void PromptRepoRemoteMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository remotes",
                [
                    new SelectionPromptOption<string>("Add remote", "add", "Add a new remote name and URL."),
                    new SelectionPromptOption<string>("Delete remote...", "delete", "Choose a configured remote to remove.")
                ],
                "Choose a remote action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "add":
                        PromptAddRemote(state);
                        break;
                    case "delete":
                        PromptDeleteRemote(state);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Remote action cancelled."));
    }

    private static void PromptRepoTagMenu(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Repository tags",
                [
                    new SelectionPromptOption<string>("Create tag", "create", "Create a tag that points to HEAD."),
                    new SelectionPromptOption<string>("Delete tag...", "delete", "Choose a local tag to delete."),
                    new SelectionPromptOption<string>("Delete remote tag...", "delete-remote", "Delete a tag from a selected remote."),
                    new SelectionPromptOption<string>("Push tags", "push-tags", "Push every local tag to the default remote.")
                ],
                "Choose a tag action. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "create":
                        PromptCreateHeadTag(state);
                        break;
                    case "delete":
                        PromptDeleteTag(state);
                        break;
                    case "delete-remote":
                        PromptDeleteRemoteTag(state);
                        break;
                    case "push-tags":
                        if (TryRunGitCommand(state, "Pushed all tags.", "Failed to push tags", "push", "--tags"))
                        {
                            InvalidateGitSidebar(state);
                        }
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Tag action cancelled."));
    }

    private static void PromptPullFromRemote(AppState state)
    {
        string currentBranch = GetGitBranchName(state.RootDirectory) ?? "main";
        PromptSelectGitItem(
            state,
            "Pull from remote",
            "Choose a remote to pull from. Enter confirms, Esc cancels.",
            GetGitRemotes(state.RootDirectory),
            remote => PromptForGitText(
                state,
                "Remote branch",
                $"Pull from {remote}. Enter the remote branch name.",
                currentBranch,
                branch =>
                {
                    if (TryRunGitCommand(state, $"Pulled {remote}/{branch}.", "Failed to pull from remote", "pull", remote, branch))
                    {
                        InvalidateGitSidebar(state);
                    }
                },
                "Pull cancelled."),
            "No remotes found.",
            "Pull cancelled.");
    }

    private static void PromptPushToRemote(AppState state)
    {
        string currentBranch = GetGitBranchName(state.RootDirectory) ?? "main";
        PromptSelectGitItem(
            state,
            "Push to remote",
            "Choose a remote to push to. Enter confirms, Esc cancels.",
            GetGitRemotes(state.RootDirectory),
            remote => PromptForGitText(
                state,
                "Remote branch",
                $"Push HEAD to {remote}. Enter the remote branch name.",
                currentBranch,
                branch =>
                {
                    if (TryRunGitCommand(state, $"Pushed HEAD to {remote}/{branch}.", "Failed to push to remote", "push", remote, $"HEAD:{branch}"))
                    {
                        InvalidateGitSidebar(state);
                    }
                },
                "Push cancelled."),
            "No remotes found.",
            "Push cancelled.");
    }

    private static void PromptPublishBranch(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        if (string.IsNullOrWhiteSpace(currentBranch))
        {
            state.AddSystemMessage("Could not publish branch: no current branch was found.");
            return;
        }

        if (TryRunGitCommand(state, $"Published branch {currentBranch}.", "Failed to publish branch", "push", "-u", "origin", currentBranch))
        {
            InvalidateGitSidebar(state);
        }
    }

    private static void PromptDiscardAllGitChanges(AppState state)
    {
        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                "Discard all changes",
                [
                    new SelectionPromptOption<bool>("Yes", true, "Restore tracked files and delete untracked files."),
                    new SelectionPromptOption<bool>("No", false, "Keep the current repository changes.")
                ],
                "This will remove every uncommitted change in the repository. Esc cancels.",
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Discard all changes cancelled.");
                    return;
                }

                if (TryRunGitCommands(
                    state,
                    "Discarded all repository changes.",
                    "Failed to discard all changes",
                    ["reset", "--hard", "HEAD"],
                    ["clean", "-fd"]))
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Discard all changes cancelled."));
    }

    private static void PromptUndoLastCommit(AppState state)
    {
        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                "Undo last commit",
                [
                    new SelectionPromptOption<bool>("Yes", true, "Move HEAD back one commit and keep the changes staged."),
                    new SelectionPromptOption<bool>("No", false, "Keep the current commit history.")
                ],
                "Undo the most recent commit with a soft reset? Esc cancels.",
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Undo last commit cancelled.");
                    return;
                }

                if (TryRunGitCommand(state, "Undid the last commit.", "Failed to undo the last commit", "reset", "--soft", "HEAD~1"))
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Undo last commit cancelled."));
    }

    private static void PromptCreateStash(AppState state, bool includeUntracked, bool stagedOnly)
    {
        string title = stagedOnly
            ? "Stash staged changes"
            : includeUntracked
                ? "Stash changes and untracked files"
                : "Stash changes";
        string description = "Enter an optional stash message. Leave it blank to use git's default message. Esc cancels.";

        PromptForGitText(
            state,
            title,
            description,
            defaultValue: null,
            onSubmitted: message =>
            {
                List<string> arguments = ["stash", "push"];
                if (includeUntracked)
                {
                    arguments.Add("--include-untracked");
                }

                if (stagedOnly)
                {
                    arguments.Add("--staged");
                }

                if (!string.IsNullOrWhiteSpace(message))
                {
                    arguments.Add("-m");
                    arguments.Add(message.Trim());
                }

                if (TryRunGitCommand(state, "Created stash entry.", "Failed to create stash", [.. arguments]))
                {
                    InvalidateGitSidebar(state);
                }
            },
            cancelledMessage: "Stash cancelled.",
            allowEmpty: true);
    }

    private static void RunGitStashAction(AppState state, string verb, string stashRef, string successMessage, string failurePrefix)
    {
        if (TryRunGitCommand(state, successMessage, failurePrefix, "stash", verb, stashRef))
        {
            InvalidateGitSidebar(state);
        }
    }

    private static void PromptDropSelectedStash(AppState state, GitStashEntry stash)
    {
        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                $"Drop {stash.Ref}",
                [
                    new SelectionPromptOption<bool>("Yes", true, "Delete the selected stash entry."),
                    new SelectionPromptOption<bool>("No", false, "Keep the stash entry.")
                ],
                stash.Description.Length == 0 ? "Delete the selected stash? Esc cancels." : stash.Description,
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Drop stash cancelled.");
                    return;
                }

                RunGitStashAction(state, "drop", stash.Ref, $"Dropped {stash.Ref}.", "Failed to drop stash");
            },
            onCancelled: _ => state.AddSystemMessage("Drop stash cancelled."));
    }

    private static void PromptDropAllStashes(AppState state)
    {
        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                "Drop all stashes",
                [
                    new SelectionPromptOption<bool>("Yes", true, "Delete every stash entry."),
                    new SelectionPromptOption<bool>("No", false, "Keep the existing stashes.")
                ],
                "Clear the entire stash list? Esc cancels.",
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Drop all stashes cancelled.");
                    return;
                }

                if (TryRunGitCommand(state, "Dropped all stashes.", "Failed to drop all stashes", "stash", "clear"))
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Drop all stashes cancelled."));
    }

    private static void OpenGitStashInReaderView(AppState state, GitStashEntry stash)
    {
        string? output = RunGit(state.RootDirectory, "stash", "show", "-p", stash.Ref);
        if (string.IsNullOrWhiteSpace(output))
        {
            state.AddSystemMessage($"Could not load {stash.Ref}.");
            return;
        }

        int width = Math.Max(20, GetWindowWidth() - 1);
        if (TryBuildGitPatchReaderLines(output, width, out IReadOnlyList<ReaderViewLine> styledLines))
        {
            EnterReaderView(
                state,
                styledLines,
                stash.Ref.ToUpperInvariant(),
                "stash diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
                startAtBottom: false);
            return;
        }

        EnterReaderView(
            state,
            output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'),
            stash.Ref.ToUpperInvariant(),
            "stash diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
            startAtBottom: false);
    }

    private static void PromptMergeBranch(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        string[] branches = GetLocalBranches(state.RootDirectory)
            .Where(branch => !string.Equals(branch, currentBranch, StringComparison.Ordinal))
            .ToArray();

        PromptSelectGitItem(
            state,
            "Merge branch",
            "Choose a local branch to merge into the current branch. Esc cancels.",
            branches,
            branch =>
            {
                if (TryRunGitCommand(state, $"Merged branch {branch}.", "Failed to merge branch", "merge", branch))
                {
                    InvalidateGitSidebar(state);
                }
            },
            "No merge targets were found.",
            "Merge cancelled.");
    }

    private static void PromptRebaseOntoBranch(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        string[] branches = GetLocalBranches(state.RootDirectory)
            .Where(branch => !string.Equals(branch, currentBranch, StringComparison.Ordinal))
            .ToArray();

        PromptSelectGitItem(
            state,
            "Rebase onto branch",
            "Choose a local branch to rebase onto. Esc cancels.",
            branches,
            branch =>
            {
                if (TryRunGitCommand(state, $"Rebased onto {branch}.", "Failed to rebase branch", "rebase", branch))
                {
                    InvalidateGitSidebar(state);
                }
            },
            "No rebase targets were found.",
            "Rebase cancelled.");
    }

    private static void PromptCreateBranchFromRef(AppState state)
    {
        string[] refs =
        [
            .. GetLocalBranches(state.RootDirectory),
            .. GetRemoteBranches(state.RootDirectory),
            .. GetGitTags(state.RootDirectory)
        ];

        string[] uniqueRefs = refs
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        PromptSelectGitItem(
            state,
            "Create branch from",
            "Choose a starting ref for the new branch. Esc cancels.",
            uniqueRefs,
            startPoint => PromptCreateBranchFromGitSidebar(
                state,
                startPoint,
                "New branch name",
                $"Create and switch to a new git branch from {startPoint}. Enter submits, Esc cancels."),
            "No branch, remote branch, or tag refs were found.",
            "Branch creation cancelled.");
    }

    private static void PromptRenameCurrentBranch(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        if (string.IsNullOrWhiteSpace(currentBranch))
        {
            state.AddSystemMessage("Could not rename branch: no current branch was found.");
            return;
        }

        PromptForGitText(
            state,
            "Rename branch",
            $"Rename {currentBranch}. Enter the new branch name.",
            currentBranch,
            newName =>
            {
                if (TryRunGitCommand(state, $"Renamed branch to {newName}.", "Failed to rename branch", "branch", "-m", newName))
                {
                    InvalidateGitSidebar(state);
                }
            },
            "Branch rename cancelled.");
    }

    private static void PromptDeleteLocalBranch(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        string[] branches = GetLocalBranches(state.RootDirectory)
            .Where(branch => !string.Equals(branch, currentBranch, StringComparison.Ordinal))
            .ToArray();

        PromptSelectGitItem(
            state,
            "Delete branch",
            "Choose a local branch to delete. Esc cancels.",
            branches,
            branch => PromptConfirmGitCommand(
                state,
                $"Delete branch {branch}",
                $"Delete local branch {branch}? Esc cancels.",
                $"Deleted branch {branch}.",
                "Failed to delete branch",
                "Branch deletion cancelled.",
                "branch",
                "-d",
                branch),
            "No deletable local branches were found.",
            "Branch deletion cancelled.");
    }

    private static void PromptDeleteRemoteBranch(AppState state)
    {
        string[] remoteBranches = GetRemoteBranches(state.RootDirectory);
        PromptSelectGitItem(
            state,
            "Delete remote branch",
            "Choose a remote branch to delete. Esc cancels.",
            remoteBranches,
            remoteBranch =>
            {
                int slashIndex = remoteBranch.IndexOf('/');
                if (slashIndex <= 0 || slashIndex >= remoteBranch.Length - 1)
                {
                    state.AddSystemMessage($"Could not delete remote branch {remoteBranch}.");
                    return;
                }

                string remote = remoteBranch[..slashIndex];
                string branch = remoteBranch[(slashIndex + 1)..];
                PromptConfirmGitCommand(
                    state,
                    $"Delete {remoteBranch}",
                    $"Delete {remoteBranch} from remote {remote}? Esc cancels.",
                    $"Deleted remote branch {remoteBranch}.",
                    "Failed to delete remote branch",
                    "Remote branch deletion cancelled.",
                    "push",
                    remote,
                    "--delete",
                    branch);
            },
            "No remote branches were found.",
            "Remote branch deletion cancelled.");
    }

    private static void PromptAddRemote(AppState state)
    {
        PromptForGitText(
            state,
            "Remote name",
            "Enter the new remote name. Esc cancels.",
            defaultValue: null,
            onSubmitted: remoteName => PromptForGitText(
                state,
                "Remote URL",
                $"Enter the URL for remote {remoteName}.",
                defaultValue: null,
                onSubmitted: remoteUrl =>
                {
                    if (TryRunGitCommand(state, $"Added remote {remoteName}.", "Failed to add remote", "remote", "add", remoteName, remoteUrl))
                    {
                        InvalidateGitSidebar(state);
                    }
                },
                cancelledMessage: "Add remote cancelled."),
            cancelledMessage: "Add remote cancelled.");
    }

    private static void PromptDeleteRemote(AppState state)
    {
        PromptSelectGitItem(
            state,
            "Delete remote",
            "Choose a remote to delete. Esc cancels.",
            GetGitRemotes(state.RootDirectory),
            remote => PromptConfirmGitCommand(
                state,
                $"Delete remote {remote}",
                $"Delete remote {remote}? Esc cancels.",
                $"Deleted remote {remote}.",
                "Failed to delete remote",
                "Delete remote cancelled.",
                "remote",
                "remove",
                remote),
            "No remotes found.",
            "Delete remote cancelled.");
    }

    private static void PromptCreateHeadTag(AppState state)
    {
        PromptForGitText(
            state,
            "New tag name",
            "Create a tag on HEAD. Enter the tag name, Esc cancels.",
            defaultValue: null,
            onSubmitted: tagName =>
            {
                if (TryRunGitCommand(state, $"Created tag {tagName}.", "Failed to create tag", "tag", tagName))
                {
                    InvalidateGitSidebar(state);
                }
            },
            cancelledMessage: "Tag creation cancelled.");
    }

    private static void PromptDeleteTag(AppState state)
    {
        PromptSelectGitItem(
            state,
            "Delete tag",
            "Choose a local tag to delete. Esc cancels.",
            GetGitTags(state.RootDirectory),
            tag => PromptConfirmGitCommand(
                state,
                $"Delete tag {tag}",
                $"Delete local tag {tag}? Esc cancels.",
                $"Deleted tag {tag}.",
                "Failed to delete tag",
                "Tag deletion cancelled.",
                "tag",
                "-d",
                tag),
            "No tags found.",
            "Tag deletion cancelled.");
    }

    private static void PromptDeleteRemoteTag(AppState state)
    {
        PromptSelectGitItem(
            state,
            "Delete remote tag",
            "Choose the remote that owns the tag deletion. Esc cancels.",
            GetGitRemotes(state.RootDirectory),
            remote => PromptSelectGitItem(
                state,
                "Delete remote tag",
                $"Choose a tag to delete from {remote}. Esc cancels.",
                GetGitTags(state.RootDirectory),
                tag => PromptConfirmGitCommand(
                    state,
                    $"Delete {tag} on {remote}",
                    $"Delete tag {tag} from remote {remote}? Esc cancels.",
                    $"Deleted remote tag {tag} from {remote}.",
                    "Failed to delete remote tag",
                    "Remote tag deletion cancelled.",
                    "push",
                    remote,
                    $":refs/tags/{tag}"),
                "No tags found.",
                "Remote tag deletion cancelled."),
            "No remotes found.",
            "Remote tag deletion cancelled.");
    }

    private static void PromptConfirmGitCommand(
        AppState state,
        string title,
        string description,
        string successMessage,
        string failurePrefix,
        string cancelledMessage,
        params string[] arguments)
    {
        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                title,
                [
                    new SelectionPromptOption<bool>("Yes", true, "Run this git action."),
                    new SelectionPromptOption<bool>("No", false, "Cancel without changing the repository.")
                ],
                description,
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage(cancelledMessage);
                    return;
                }

                if (TryRunGitCommand(state, successMessage, failurePrefix, arguments))
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage(cancelledMessage));
    }

    private static void PromptForGitText(
        AppState state,
        string title,
        string description,
        string? defaultValue,
        Action<string> onSubmitted,
        string cancelledMessage,
        bool allowEmpty = false)
    {
        state.ActiveModal = TextModalState.Create(
            new TextPromptRequest(
                title,
                description,
                DefaultValue: defaultValue,
                AllowCancellation: true),
            isSecret: false,
            completionToken: new object(),
            onSubmitted: value =>
            {
                string trimmed = value.Trim();
                if (!allowEmpty && trimmed.Length == 0)
                {
                    state.AddSystemMessage($"{title} cancelled: value cannot be empty.");
                    return;
                }

                onSubmitted(allowEmpty ? trimmed : trimmed);
            },
            onCancelled: _ => state.AddSystemMessage(cancelledMessage));
    }

    private static void PromptSelectGitItem(
        AppState state,
        string title,
        string description,
        IReadOnlyList<string> items,
        Action<string> onSelected,
        string emptyMessage,
        string cancelledMessage)
    {
        if (items.Count == 0)
        {
            state.AddSystemMessage(emptyMessage);
            return;
        }

        SelectionPromptOption<string>[] options = items
            .Select(item => new SelectionPromptOption<string>(
                TruncateFromRight(item, 80),
                item,
                item))
            .ToArray();

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                title,
                options,
                description,
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected,
            onCancelled: _ => state.AddSystemMessage(cancelledMessage));
    }

    private static void PromptSelectStash(
        AppState state,
        string title,
        string description,
        Action<GitStashEntry> onSelected,
        string emptyMessage,
        string cancelledMessage)
    {
        GitStashEntry[] stashes = GetGitStashes(state.RootDirectory);
        if (stashes.Length == 0)
        {
            state.AddSystemMessage(emptyMessage);
            return;
        }

        SelectionPromptOption<GitStashEntry>[] options = stashes
            .Select(stash => new SelectionPromptOption<GitStashEntry>(
                TruncateFromRight($"{stash.Ref} {stash.Description}".Trim(), 80),
                stash,
                stash.Description))
            .ToArray();

        state.ActiveModal = SelectionModalState<GitStashEntry>.Create(
            new SelectionPromptRequest<GitStashEntry>(
                title,
                options,
                description,
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected,
            onCancelled: _ => state.AddSystemMessage(cancelledMessage));
    }

    private static void PromptCommitActionFromGitSidebar(AppState state, GitSidebarLine selected)
    {
        if (string.IsNullOrWhiteSpace(selected.CommitHash))
        {
            state.AddSystemMessage("Select a commit row first.");
            return;
        }

        string shortHash = selected.CommitHash!.Length > 8
            ? selected.CommitHash[..8]
            : selected.CommitHash;
        string message = string.IsNullOrWhiteSpace(selected.CommitMessage)
            ? "(no message)"
            : selected.CommitMessage!;
        GitCommitDetails? details = TryGetGitCommitDetails(state.RootDirectory, selected.CommitHash!, out GitCommitDetails loadedDetails)
            ? loadedDetails
            : null;
        string? githubUrl = TryBuildGitHubCommitUrl(state.RootDirectory, selected.CommitHash!);
        string description = BuildCommitActionDescription(message, details);

        List<SelectionPromptOption<string>> options =
        [
            new("Open changes", "diff", "Show this commit diff inside the CLI reader view."),
            new("Copy hash", "copy-hash", "Copy the selected commit hash to the clipboard."),
            new("Copy message", "copy-message", "Copy the selected commit message to the clipboard."),
            new("View files", "view-files", "List the file names included in this commit."),
            new("Cherry-pick", "cherry-pick", "Apply this commit onto the current branch."),
            new("Create branch", "create-branch", "Create and switch to a new branch from this commit."),
            new("Create tag", "create-tag", "Create a tag that points at this commit.")
        ];

        if (!string.IsNullOrWhiteSpace(details?.AuthorEmail))
        {
            options.Insert(3, new SelectionPromptOption<string>("Email author", "email-author", "Open your default mail app with the author's address."));
        }

        if (!string.IsNullOrWhiteSpace(githubUrl))
        {
            options.Add(new SelectionPromptOption<string>("Open on GitHub", "open-github", "Open this commit in the browser."));
        }

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                $"Commit {shortHash}",
                [.. options],
                description,
                DefaultIndex: 0,
                AllowCancellation: true,
                DescriptionSupportsMarkup: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "diff":
                        OpenCommitDiffInReaderView(state, selected);
                        break;
                    case "copy-hash":
                        CopyGitSidebarCommitText(state, selected.CommitHash!, "commit hash");
                        break;
                    case "copy-message":
                        CopyGitSidebarCommitText(state, selected.CommitMessage ?? string.Empty, "commit message");
                        break;
                    case "email-author":
                        OpenGitCommitAuthorEmail(state, details);
                        break;
                    case "view-files":
                        OpenGitCommitFilesInReaderView(state, selected, details);
                        break;
                    case "cherry-pick":
                        PromptCherryPickCommitFromGitSidebar(state, selected);
                        break;
                    case "create-branch":
                        PromptCreateBranchFromGitSidebar(
                            state,
                            selected.CommitHash,
                            "New branch from commit",
                            "Create and switch to a new git branch from the selected commit. Enter submits, Esc cancels.");
                        break;
                    case "create-tag":
                        PromptCreateTagFromGitSidebar(state, selected);
                        break;
                    case "open-github":
                        OpenGitCommitOnGitHub(state, githubUrl);
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("Commit action cancelled."));
    }

    private static void PromptFileActionFromGitSidebar(AppState state, GitSidebarLine selected)
    {
        if (string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            state.AddSystemMessage("Select a git file row first.");
            return;
        }

        string relativePath = selected.RelativePath!;
        List<SelectionPromptOption<string>> options =
        [
            new("Open", "open", "Open the selected file in your editor or system opener."),
            new("Open changes", "open-changes", "Show the selected file patch inside the CLI reader view."),
            new(
                selected.Kind == GitSidebarLineKind.StagedFile ? "Unstage" : "Stage",
                "toggle-stage",
                selected.Kind == GitSidebarLineKind.StagedFile
                    ? "Remove this file from the git index."
                    : "Add this file to the git index."),
            new("Add to .gitignore", "gitignore", "Append this relative path to the workspace .gitignore."),
            new("Add to .stemcodeignore", "stemcodeignore", "Append this relative path to the workspace .stemcodeignore."),
            new("Reveal in Explorer", "reveal", "Reveal the selected file in the system file manager."),
            new("Copy patch", "copy-patch", "Copy the selected file patch to the clipboard."),
            new("Copy relative path", "copy-relative-path", "Copy the repository-relative path to the clipboard.")
        ];

        if (selected.Kind == GitSidebarLineKind.ChangedFile)
        {
            options.Insert(
                3,
                new SelectionPromptOption<string>(
                    "Discard",
                    "discard",
                    "Discard this file's worktree edits or delete it if it is untracked."));
        }

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                $"File {TruncateFromRight(relativePath, 80)}",
                [.. options],
                "Choose an action for the selected git file. Enter confirms, Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                switch (action)
                {
                    case "open":
                        if (selected.FilePath is string path)
                        {
                            OpenFileInEditor(state, path);
                        }
                        break;
                    case "open-changes":
                        OpenGitSidebarFileChanges(state, selected);
                        break;
                    case "toggle-stage":
                        ToggleGitSidebarStageSelection(state);
                        break;
                    case "discard":
                        PromptDiscardGitSidebarSelection(state);
                        break;
                    case "gitignore":
                        AddGitSidebarFileToIgnoreFile(state, selected, ".gitignore");
                        break;
                    case "stemcodeignore":
                        AddGitSidebarFileToIgnoreFile(state, selected, ".stemcodeignore");
                        break;
                    case "reveal":
                        RevealGitSidebarFileInExplorer(state, selected);
                        break;
                    case "copy-patch":
                        CopyGitSidebarFilePatch(state, selected);
                        break;
                    case "copy-relative-path":
                        CopyGitSidebarCommitText(state, relativePath, "relative path");
                        break;
                }
            },
            onCancelled: _ => state.AddSystemMessage("File action cancelled."));
    }

    private static void CopyGitSidebarCommitText(AppState state, string text, string description)
    {
        if (string.IsNullOrEmpty(text))
        {
            state.AddSystemMessage($"Could not copy {description}: value is empty.");
            return;
        }

        state.AddSystemMessage(TryWriteClipboardText(text)
            ? $"Copied {description} to the clipboard."
            : $"Could not copy {description}: no clipboard tool was found.");
    }

    private static void PromptCherryPickCommitFromGitSidebar(AppState state, GitSidebarLine selected)
    {
        if (string.IsNullOrWhiteSpace(selected.CommitHash))
        {
            return;
        }

        string shortHash = selected.CommitHash!.Length > 8
            ? selected.CommitHash[..8]
            : selected.CommitHash;

        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                $"Cherry-pick {shortHash}",
                [
                    new SelectionPromptOption<bool>("Yes", true, "Apply this commit to the current branch."),
                    new SelectionPromptOption<bool>("No", false, "Cancel without changing git history.")
                ],
                "Cherry-pick the selected commit onto the current branch? Esc cancels.",
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Cherry-pick cancelled.");
                    return;
                }

                if (TryRunGitCommand(
                    state,
                    $"Cherry-picked {shortHash}.",
                    "Failed to cherry-pick commit",
                    "cherry-pick",
                    selected.CommitHash!))
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Cherry-pick cancelled."));
    }

    private static void OpenCommitDiffInReaderView(AppState state, GitSidebarLine selected)
    {
        if (string.IsNullOrWhiteSpace(selected.CommitHash))
        {
            return;
        }

        string? output = RunGit(
            state.RootDirectory,
            "show",
            "--stat",
            "--patch",
            "--decorate=short",
            "--color=never",
            selected.CommitHash!);

        if (string.IsNullOrWhiteSpace(output))
        {
            state.AddSystemMessage("Could not load commit changes.");
            return;
        }

        string shortHash = selected.CommitHash!.Length > 8
            ? selected.CommitHash[..8]
            : selected.CommitHash;
        string title = string.IsNullOrWhiteSpace(selected.CommitMessage)
            ? $"COMMIT {shortHash}"
            : $"COMMIT {shortHash} {selected.CommitMessage}";
        int width = Math.Max(20, GetWindowWidth() - 1);

        if (TryBuildGitPatchReaderLines(output, width, out IReadOnlyList<ReaderViewLine> styledLines))
        {
            EnterReaderView(
                state,
                styledLines,
                title,
                "commit diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
                startAtBottom: false);
            return;
        }

        string[] lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        EnterReaderView(
            state,
            lines,
            title,
            "commit diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
            startAtBottom: false);
    }

    private static void OpenGitSidebarFileChanges(AppState state, GitSidebarLine selected)
    {
        if (!TryGetGitSidebarFilePatch(state, selected, out string patch))
        {
            state.AddSystemMessage("Could not load file changes.");
            return;
        }

        string relativePath = selected.RelativePath ?? selected.FilePath ?? "file";
        string titlePrefix = selected.Kind == GitSidebarLineKind.StagedFile ? "STAGED" : "CHANGES";
        int width = Math.Max(20, GetWindowWidth() - 1);

        if (TryBuildGitPatchReaderLines(patch, width, out IReadOnlyList<ReaderViewLine> styledLines))
        {
            EnterReaderView(
                state,
                styledLines,
                $"{titlePrefix} {relativePath}",
                "file diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
                startAtBottom: false);
            return;
        }

        string[] lines = patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        EnterReaderView(
            state,
            lines,
            $"{titlePrefix} {relativePath}",
            "file diff | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
            startAtBottom: false);
    }

    private static void CopyGitSidebarFilePatch(AppState state, GitSidebarLine selected)
    {
        if (!TryGetGitSidebarFilePatch(state, selected, out string patch))
        {
            state.AddSystemMessage("Could not copy patch: no diff was available.");
            return;
        }

        state.AddSystemMessage(TryWriteClipboardText(patch)
            ? "Copied file patch to the clipboard."
            : "Could not copy file patch: no clipboard tool was found.");
    }

    private static bool TryGetGitSidebarFilePatch(AppState state, GitSidebarLine selected, out string patch)
    {
        patch = string.Empty;

        if (string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            return false;
        }

        string relativePath = selected.RelativePath!;
        string[] arguments = selected.Kind == GitSidebarLineKind.StagedFile
            ? ["diff", "--cached", "--no-ext-diff", "--color=never", "--", relativePath]
            : ["diff", "--no-ext-diff", "--color=never", "--", relativePath];
        (bool succeeded, string? output, string? error) = RunGitWithResult(state.RootDirectory, arguments);

        if (succeeded && !string.IsNullOrWhiteSpace(output))
        {
            patch = output;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            patch = output;
            return true;
        }

        if (selected.Kind == GitSidebarLineKind.ChangedFile &&
            string.Equals(selected.StatusCode, "?", StringComparison.Ordinal) &&
            selected.FilePath is string filePath &&
            File.Exists(filePath) &&
            TryBuildUntrackedGitSidebarPatch(relativePath, filePath, out patch))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            patch = error!;
            return true;
        }

        return false;
    }

    private static bool TryBuildUntrackedGitSidebarPatch(string relativePath, string filePath, out string patch)
    {
        patch = string.Empty;

        string temporaryFile = Path.GetTempFileName();
        try
        {
            string? output = RunGitAllowingDiffExitCode(
                Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory(),
                1,
                "diff",
                "--no-index",
                "--no-ext-diff",
                "--color=never",
                "--label",
                $"a/{relativePath}",
                "--label",
                $"b/{relativePath}",
                "--",
                temporaryFile,
                filePath);

            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            patch = output
                .Replace(temporaryFile, $"/dev/null", StringComparison.Ordinal)
                .Replace(Path.GetFileName(temporaryFile), "dev-null", StringComparison.Ordinal);
            return true;
        }
        finally
        {
            try
            {
                File.Delete(temporaryFile);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string? RunGitAllowingDiffExitCode(
        string workingDirectory,
        int allowedExitCode,
        params string[] arguments)
    {
        try
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

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!TryWaitForProcessExit(process, 4000))
            {
                return null;
            }

            string output = outputTask.GetAwaiter().GetResult().Trim();
            string error = errorTask.GetAwaiter().GetResult().Trim();
            return process.ExitCode is 0 || process.ExitCode == allowedExitCode
                ? (!string.IsNullOrWhiteSpace(output) ? output : error)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void AddGitSidebarFileToIgnoreFile(AppState state, GitSidebarLine selected, string ignoreFileName)
    {
        if (string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            state.AddSystemMessage($"Could not update {ignoreFileName}: file path was empty.");
            return;
        }

        string relativePath = selected.RelativePath!.Replace('\\', '/');
        string ignoreFilePath = Path.Combine(state.RootDirectory, ignoreFileName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ignoreFilePath) ?? state.RootDirectory);

            string normalizedLine = relativePath.Trim();
            string existing = File.Exists(ignoreFilePath)
                ? File.ReadAllText(ignoreFilePath)
                : string.Empty;
            string[] existingLines = existing
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');

            if (existingLines.Any(line => NormalizeIgnoreEntry(line) == NormalizeIgnoreEntry(normalizedLine)))
            {
                state.AddSystemMessage($"{relativePath} is already listed in {ignoreFileName}.");
                return;
            }

            string prefix = existing.Length == 0 || existing.EndsWith('\n')
                ? string.Empty
                : Environment.NewLine;
            File.AppendAllText(ignoreFilePath, $"{prefix}{normalizedLine}{Environment.NewLine}");
            state.AddSystemMessage($"Added {relativePath} to {ignoreFileName}.");
        }
        catch (Exception exception)
        {
            state.AddSystemMessage($"Could not update {ignoreFileName}: {exception.Message}");
        }
    }

    private static string NormalizeIgnoreEntry(string line)
    {
        return line
            .Trim()
            .TrimStart('.', '/', '\\')
            .Replace('\\', '/');
    }

    private static void RevealGitSidebarFileInExplorer(AppState state, GitSidebarLine selected)
    {
        string? filePath = selected.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            state.AddSystemMessage("Could not reveal file: path was empty.");
            return;
        }

        string targetDirectory = File.Exists(filePath)
            ? Path.GetDirectoryName(filePath) ?? state.RootDirectory
            : Directory.Exists(filePath)
                ? filePath
                : Path.GetDirectoryName(filePath) ?? state.RootDirectory;

        bool opened = OperatingSystem.IsWindows()
            ? TryStartProcess("explorer.exe", $"/select,\"{filePath}\"", useShell: false) ||
                TryStartProcess("explorer.exe", $"\"{targetDirectory}\"", useShell: false)
            : OperatingSystem.IsMacOS()
                ? TryStartProcess("open", $"-R \"{filePath}\"", useShell: false) ||
                    TryStartProcess("open", $"\"{targetDirectory}\"", useShell: false)
                : TryStartProcess("xdg-open", $"\"{targetDirectory}\"", useShell: false);

        if (!opened)
        {
            state.AddSystemMessage($"Could not reveal {filePath}.");
        }
    }

    private static void PromptDiscardGitSidebarSelection(AppState state)
    {
        GitSidebarLine? selected = GetSelectedGitSidebarLine(state);
        if (selected is null)
        {
            return;
        }

        if (selected.Kind != GitSidebarLineKind.ChangedFile || string.IsNullOrWhiteSpace(selected.RelativePath))
        {
            state.AddSystemMessage("Select a file in Changes to discard its worktree edits.");
            return;
        }

        string relativePath = selected.RelativePath;
        string action = string.Equals(selected.StatusCode, "?", StringComparison.Ordinal)
            ? "Delete untracked file"
            : "Discard file changes";
        string description = string.Equals(selected.StatusCode, "?", StringComparison.Ordinal)
            ? $"Remove {relativePath} from disk. Esc cancels."
            : $"Restore {relativePath} to the version in git. Esc cancels.";

        state.ActiveModal = SelectionModalState<bool>.Create(
            new SelectionPromptRequest<bool>(
                action,
                [
                    new SelectionPromptOption<bool>("Yes", true, "Apply the discard action."),
                    new SelectionPromptOption<bool>("No", false, "Keep the current file changes.")
                ],
                description,
                DefaultIndex: 1,
                AllowCancellation: true),
            new object(),
            onSelected: confirmed =>
            {
                if (!confirmed)
                {
                    state.AddSystemMessage("Discard cancelled.");
                    return;
                }

                bool success = string.Equals(selected.StatusCode, "?", StringComparison.Ordinal)
                    ? TryRunGitCommand(
                        state,
                        $"Deleted untracked file {relativePath}.",
                        "Failed to delete untracked file",
                        "clean",
                        "-f",
                        "--",
                        relativePath)
                    : TryRunGitCommand(
                        state,
                        $"Discarded changes in {relativePath}.",
                        "Failed to discard file changes",
                        "restore",
                        "--",
                        relativePath);

                if (success)
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Discard cancelled."));
    }

    private static void PromptCommitFromGitSidebar(AppState state)
    {
        string? staged = RunGit(state.RootDirectory, "diff", "--cached", "--name-only");
        if (string.IsNullOrWhiteSpace(staged))
        {
            state.AddSystemMessage("Stage at least one file before committing.");
            return;
        }

        if (!state.IsReady)
        {
            state.AddSystemMessage("StemCode is still starting, so enter the commit message manually.");
            ShowCommitMessagePrompt(state, defaultValue: null);
            return;
        }

        StartCommitMessageSuggestion(state, staged);
    }

    private static void StartCommitMessageSuggestion(AppState state, string stagedFiles)
    {
        string diffSummary = BuildCommitMessageDiffSummary(state.RootDirectory);
        string prompt = BuildCommitMessageSuggestionPrompt(stagedFiles, diffSummary);

        state.ResetTurnCancellation();
        state.TurnCancellation = CancellationTokenSource.CreateLinkedTokenSource(state.LifetimeCancellation.Token);
        long operationId = state.BeginTrackedOperation();
        state.UiBridge.SetActiveCliOperation(operationId);
        state.IsBusy = true;
        state.ClearBusyWhenStreamCompletes = false;
        state.CurrentTurnStartedAt = DateTimeOffset.UtcNow;
        state.PendingCompletionNote = null;
        state.ActivityText = "Drafting commit message";

        SilentUiBridge silentUiBridge = new();
        state.ActiveOperation = Task.Run(async () =>
        {
            try
            {
                ConversationTurnResult result = await state.Backend.RunTurnAsync(
                    prompt,
                    silentUiBridge,
                    state.TurnCancellation!.Token);

                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    FinishCommitMessageSuggestion(appState);

                    string suggestion = SanitizeCommitMessageSuggestion(result.ResponseText);
                    if (string.IsNullOrWhiteSpace(suggestion))
                    {
                        appState.AddSystemMessage("AI commit message was empty. Enter the message manually.");
                        ShowCommitMessagePrompt(appState, defaultValue: null);
                        return;
                    }

                    ShowCommitMessagePrompt(appState, suggestion);
                });
            }
            catch (OperationCanceledException) when (state.LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (state.TurnCancellation?.IsCancellationRequested == true)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    FinishCommitMessageSuggestion(appState);
                    appState.AddSystemMessage("Commit message generation cancelled.");
                    TryStartNextPendingSubmission(appState);
                });
            }
            catch (Exception exception)
            {
                state.UiBridge.Enqueue(appState =>
                {
                    if (!appState.IsTrackedOperationCurrent(operationId))
                    {
                        return;
                    }

                    FinishCommitMessageSuggestion(appState);
                    appState.AddSystemMessage($"Could not generate an AI commit message: {exception.Message}");
                    ShowCommitMessagePrompt(appState, defaultValue: null);
                });
            }
        });
    }

    private static void FinishCommitMessageSuggestion(AppState state)
    {
        state.IsBusy = false;
        state.ActiveOperation = null;
        state.ClearBusyWhenStreamCompletes = false;
        state.CurrentTurnStartedAt = null;
        state.PendingCompletionNote = null;
        state.ActivityText = state.IsReady ? "Ready" : "Idle";
        state.ResetTurnCancellation();
        state.IsTurnInterruptPending = false;
    }

    private static void ShowCommitMessagePrompt(AppState state, string? defaultValue)
    {
        state.ActiveModal = TextModalState.Create(
            new TextPromptRequest(
                "Commit message",
                "Review or edit the commit message, then press Enter to commit. Esc cancels.",
                DefaultValue: defaultValue,
                AllowCancellation: true),
            isSecret: false,
            completionToken: new object(),
            onSubmitted: message =>
            {
                string trimmed = message.Trim();
                if (trimmed.Length == 0)
                {
                    state.AddSystemMessage("Commit cancelled: message cannot be empty.");
                    return;
                }

                string[] arguments = BuildGitSidebarCommitArguments(trimmed);
                bool success = TryRunGitCommand(
                    state,
                    $"Committed staged changes: {trimmed}",
                    "Failed to create commit",
                    arguments);

                if (success)
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Commit cancelled."));
    }

    private static string BuildCommitMessageSuggestionPrompt(string stagedFiles, string diffSummary)
    {
        return
            "Write a git commit message for the staged changes below." + Environment.NewLine +
            "Return exactly one line: no bullets, no quotes, no code fences, and no explanation." + Environment.NewLine +
            $"Use imperative mood and keep it within {GitSidebarCommitMessageSuggestionMaxLength} characters. Prefer a conventional-commit prefix when it clearly fits." + Environment.NewLine +
            Environment.NewLine +
            "Staged files:" + Environment.NewLine +
            stagedFiles.Trim() + Environment.NewLine +
            Environment.NewLine +
            "Staged diff summary:" + Environment.NewLine +
            diffSummary;
    }

    private static string BuildCommitMessageDiffSummary(string root)
    {
        string stats = RunGit(root, "diff", "--cached", "--stat", "--summary", "--find-renames") ?? string.Empty;
        string patch = RunGit(root, "diff", "--cached", "--unified=0", "--no-ext-diff", "--no-color", "--minimal")
            ?? string.Empty;

        string combined = string.IsNullOrWhiteSpace(patch)
            ? stats
            : string.IsNullOrWhiteSpace(stats)
                ? patch
                : stats.TrimEnd() + Environment.NewLine + Environment.NewLine + patch.TrimStart();

        string normalized = combined
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Trim();

        if (normalized.Length == 0)
        {
            return "(no diff details available)";
        }

        const int maxLength = 12000;
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + Environment.NewLine + "[truncated]";
    }

    private static string SanitizeCommitMessageSuggestion(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        string[] lines = responseText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return string.Empty;
        }

        string suggestion = string.Empty;
        foreach (string line in lines)
        {
            string candidate = line.Trim();
            if (candidate.StartsWith("```", StringComparison.Ordinal) ||
                candidate == "```")
            {
                continue;
            }

            candidate = candidate
                .Trim('`', '"', '\'')
                .TrimStart('-', '*', ' ');

            if (candidate.Length == 0)
            {
                continue;
            }

            suggestion = candidate;
            break;
        }

        if (suggestion.Length == 0)
        {
            return string.Empty;
        }

        return suggestion.Length <= GitSidebarCommitMessageSuggestionMaxLength
            ? suggestion
            : suggestion[..GitSidebarCommitMessageSuggestionMaxLength].TrimEnd();
    }

    private static string[] BuildGitSidebarCommitArguments(string message)
    {
        return
        [
            "commit",
            "-m",
            message,
            "-m",
            GitSidebarCommitCoAuthorTrailer
        ];
    }

    private sealed class SilentUiBridge : IUiBridge
    {
        public Task<T> RequestSelectionAsync<T>(SelectionPromptRequest<T> request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Commit message suggestion cannot request a selection.");

        public Task<string> RequestTextAsync(TextPromptRequest request, bool isSecret, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Commit message suggestion cannot request text input.");

        public void ShowError(string message)
        {
        }

        public void ShowInfo(string message)
        {
        }

        public void ShowSuccess(string message)
        {
        }

        public void ShowAssistantMessageChunk(string text)
        {
        }

        public void ShowAssistantReasoning(string reasoningText)
        {
        }

        public void ShowToolCalls(IReadOnlyList<ConversationToolCall> toolCalls)
        {
        }

        public void ShowToolResults(ToolExecutionBatchResult toolExecutionResult)
        {
        }

        public void ShowExecutionPlan(ExecutionPlanProgress progress)
        {
        }

        public void ShowProviderRetry(ProviderRetryProgress progress)
        {
        }
    }

    private static void PromptBranchActionFromGitSidebar(AppState state)
    {
        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Branch actions",
                [
                    new SelectionPromptOption<string>("Switch branch", "switch", "Move this worktree to an existing local branch."),
                    new SelectionPromptOption<string>("Create branch", "create", "Create and switch to a new branch from the current HEAD.")
                ],
                "Pick a branch action. Esc cancels.",
                DefaultIndex: 0,
                AllowCancellation: true),
            new object(),
            onSelected: action =>
            {
                if (string.Equals(action, "switch", StringComparison.Ordinal))
                {
                    PromptSwitchBranchFromGitSidebar(state);
                    return;
                }

                PromptCreateBranchFromGitSidebar(
                    state,
                    startPoint: null,
                    "New branch name",
                    "Create and switch to a new git branch. Enter submits, Esc cancels.");
            },
            onCancelled: _ => state.AddSystemMessage("Branch action cancelled."));
    }

    private static void PromptSwitchBranchFromGitSidebar(AppState state)
    {
        string? currentBranch = GetGitBranchName(state.RootDirectory);
        string? output = RunGit(state.RootDirectory, "branch", "--format=%(refname:short)");
        string[] branches = output?
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];

        if (branches.Length == 0)
        {
            state.AddSystemMessage("No local branches found.");
            return;
        }

        SelectionPromptOption<string>[] options = branches
            .Select(branch => new SelectionPromptOption<string>(
                branch == currentBranch ? $"Current: {branch}" : branch,
                branch,
                "Switch to this branch."))
            .ToArray();

        int defaultIndex = Array.FindIndex(branches, branch => string.Equals(branch, currentBranch, StringComparison.Ordinal));
        if (defaultIndex < 0)
        {
            defaultIndex = 0;
        }

        state.ActiveModal = SelectionModalState<string>.Create(
            new SelectionPromptRequest<string>(
                "Switch branch",
                options,
                "Choose a local branch. Esc cancels.",
                DefaultIndex: defaultIndex,
                AllowCancellation: true),
            new object(),
            onSelected: branch =>
            {
                bool success = TryRunGitCommand(
                    state,
                    $"Switched to branch {branch}.",
                    "Failed to switch branch",
                    "switch",
                    branch);

                if (success)
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Branch switch cancelled."));
    }

    private static void PromptCreateBranchFromGitSidebar(
        AppState state,
        string? startPoint,
        string title,
        string description)
    {
        state.ActiveModal = TextModalState.Create(
            new TextPromptRequest(
                title,
                description,
                DefaultValue: null,
                AllowCancellation: true),
            isSecret: false,
            completionToken: new object(),
            onSubmitted: branchName =>
            {
                string trimmed = branchName.Trim();
                if (trimmed.Length == 0)
                {
                    state.AddSystemMessage("Branch creation cancelled: name cannot be empty.");
                    return;
                }

                List<string> arguments = ["switch", "-c", trimmed];
                if (!string.IsNullOrWhiteSpace(startPoint))
                {
                    arguments.Add(startPoint!);
                }

                bool success = TryRunGitCommand(
                    state,
                    $"Created and switched to branch {trimmed}.",
                    "Failed to create branch",
                    [.. arguments]);

                if (success)
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Branch creation cancelled."));
    }

    private static void PromptCreateTagFromGitSidebar(AppState state, GitSidebarLine selected)
    {
        if (string.IsNullOrWhiteSpace(selected.CommitHash))
        {
            return;
        }

        state.ActiveModal = TextModalState.Create(
            new TextPromptRequest(
                "New tag name",
                "Create a git tag that points to the selected commit. Enter submits, Esc cancels.",
                DefaultValue: null,
                AllowCancellation: true),
            isSecret: false,
            completionToken: new object(),
            onSubmitted: tagName =>
            {
                string trimmed = tagName.Trim();
                if (trimmed.Length == 0)
                {
                    state.AddSystemMessage("Tag creation cancelled: name cannot be empty.");
                    return;
                }

                bool success = TryRunGitCommand(
                    state,
                    $"Created tag {trimmed}.",
                    "Failed to create tag",
                    "tag",
                    trimmed,
                    selected.CommitHash!);

                if (success)
                {
                    InvalidateGitSidebar(state);
                }
            },
            onCancelled: _ => state.AddSystemMessage("Tag creation cancelled."));
    }

    private static void RunGitPushFromSidebar(AppState state)
    {
        if (TryRunGitCommand(
            state,
            "Git push completed.",
            "Failed to push",
            "push"))
        {
            InvalidateGitSidebar(state);
        }
    }

    private static void RunGitPullFromSidebar(AppState state)
    {
        if (TryRunGitCommand(
            state,
            "Git pull completed.",
            "Failed to pull",
            "pull"))
        {
            InvalidateGitSidebar(state);
        }
    }

    private static bool TryRunGitCommand(
        AppState state,
        string successMessage,
        string failurePrefix,
        params string[] arguments)
    {
        (bool succeeded, string? output, string? error) = RunGitWithResult(state.RootDirectory, arguments);
        if (succeeded)
        {
            state.AddSystemMessage(successMessage);
            return true;
        }

        string detail = !string.IsNullOrWhiteSpace(error)
            ? error
            : output ?? "git command failed.";
        state.AddSystemMessage($"{failurePrefix}: {detail}");
        return false;
    }

    private static bool TryRunGitCommands(
        AppState state,
        string successMessage,
        string failurePrefix,
        params string[][] commandSets)
    {
        foreach (string[] command in commandSets)
        {
            (bool succeeded, string? output, string? error) = RunGitWithResult(state.RootDirectory, command);
            if (succeeded)
            {
                continue;
            }

            string detail = !string.IsNullOrWhiteSpace(error)
                ? error
                : output ?? "git command failed.";
            state.AddSystemMessage($"{failurePrefix}: {detail}");
            return false;
        }

        state.AddSystemMessage(successMessage);
        return true;
    }

    private static (bool Succeeded, string? Output, string? Error) RunGitWithResult(
        string workingDirectory,
        params string[] arguments)
    {
        try
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

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, null, "Could not start git.");
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();

            if (!TryWaitForProcessExit(process, 4000))
            {
                return (false, null, "git timed out.");
            }

            string output = outputTask.GetAwaiter().GetResult().TrimEnd();
            string error = errorTask.GetAwaiter().GetResult().TrimEnd();
            return (process.ExitCode == 0, output, error);
        }
        catch (Exception exception)
        {
            return (false, null, exception.Message);
        }
    }

    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        (bool succeeded, string? output, _) = RunGitWithResult(workingDirectory, arguments);
        return succeeded ? output : null;
    }

    private static string[] GetGitRemotes(string root)
    {
        return SplitGitOutputLines(RunGit(root, "remote"));
    }

    private static string[] GetLocalBranches(string root)
    {
        return SplitGitOutputLines(RunGit(root, "branch", "--format=%(refname:short)"));
    }

    private static string[] GetRemoteBranches(string root)
    {
        return SplitGitOutputLines(RunGit(root, "branch", "-r", "--format=%(refname:short)"))
            .Where(static branch => !branch.Contains("->", StringComparison.Ordinal))
            .ToArray();
    }

    private static string[] GetGitTags(string root)
    {
        return SplitGitOutputLines(RunGit(root, "tag", "--list", "--sort=-creatordate"));
    }

    private static GitStashEntry[] GetGitStashes(string root)
    {
        string[] lines = SplitGitOutputLines(RunGit(root, "stash", "list", "--format=%gd%x09%s"));
        return lines
            .Select(line =>
            {
                string[] parts = line.Split('\t', 2);
                string reference = parts.Length > 0 ? parts[0] : line;
                string description = parts.Length > 1 ? parts[1] : string.Empty;
                return new GitStashEntry(reference, description);
            })
            .ToArray();
    }

    private static string[] SplitGitOutputLines(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void LoadMoreGitSidebarCommits(AppState state)
    {
        state.GitSidebarCommitDisplayCount += GitSidebarCommitPageSize;
        InvalidateGitSidebar(state);
    }

    private static string BuildCommitActionDescription(string fallbackMessage, GitCommitDetails? details)
    {
        if (details is null)
        {
            return TruncateFromRight(fallbackMessage, 80);
        }

        string timestamp = details.Timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "Unknown";
        return string.Join(
            '\n',
            [
                $"[grey]Hash:[/] [yellow]{Markup.Escape(details.FullHash)}[/]",
                $"[grey]Message:[/] [white]{Markup.Escape(details.Message)}[/]",
                $"[grey]Additions:[/] [green]+{details.TotalAdditions}[/]  [grey]Subtractions:[/] [red]-{details.TotalDeletions}[/]",
                $"[grey]Author:[/] [aqua]{Markup.Escape(details.AuthorName)}[/]",
                $"[grey]Email:[/] [underline]{Markup.Escape(details.AuthorEmail)}[/]",
                $"[grey]Date:[/] {Markup.Escape(timestamp)}"
            ]);
    }

    private static bool TryGetGitCommitDetails(string root, string commitHash, out GitCommitDetails details)
    {
        details = default!;

        string? summary = RunGit(
            root,
            "show",
            "-s",
            "--date=iso-strict",
            "--format=%H%x1f%s%x1f%an%x1f%ae%x1f%ad",
            commitHash);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        string[] parts = summary.Split('\x1f');
        if (parts.Length < 5)
        {
            return false;
        }

        string? numstat = RunGit(root, "show", "--numstat", "--format=", commitHash);
        int additions = 0;
        int deletions = 0;
        List<string> files = [];

        if (!string.IsNullOrWhiteSpace(numstat))
        {
            foreach (string line in numstat.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] columns = line.Split('\t');
                if (columns.Length < 3)
                {
                    continue;
                }

                if (int.TryParse(columns[0], out int added))
                {
                    additions += added;
                }

                if (int.TryParse(columns[1], out int deleted))
                {
                    deletions += deleted;
                }

                files.Add(columns[2]);
            }
        }

        DateTimeOffset? timestamp = DateTimeOffset.TryParse(parts[4], out DateTimeOffset parsedTimestamp)
            ? parsedTimestamp
            : null;

        details = new GitCommitDetails(
            parts[0],
            parts[1],
            parts[2],
            parts[3],
            timestamp,
            additions,
            deletions,
            files);
        return true;
    }

    private static void OpenGitCommitFilesInReaderView(AppState state, GitSidebarLine selected, GitCommitDetails? details)
    {
        IReadOnlyList<string> files = details?.Files ?? [];
        if (files.Count == 0)
        {
            string? output = RunGit(state.RootDirectory, "show", "--name-only", "--format=", selected.CommitHash!);
            files = output?
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
        }

        if (files.Count == 0)
        {
            state.AddSystemMessage("No file names were found for this commit.");
            return;
        }

        string shortHash = selected.CommitHash!.Length > 8 ? selected.CommitHash[..8] : selected.CommitHash;
        EnterReaderView(
            state,
            [.. files],
            $"FILES {shortHash}",
            "commit files | Up/Down PgUp/PgDn Home/End scroll | F5 exit",
            startAtBottom: false);
    }

    private static void OpenGitCommitAuthorEmail(AppState state, GitCommitDetails? details)
    {
        if (details is null || string.IsNullOrWhiteSpace(details.AuthorEmail))
        {
            state.AddSystemMessage("Could not open email: author email is unavailable.");
            return;
        }

        string uri = $"mailto:{Uri.EscapeDataString(details.AuthorEmail)}";
        if (!TryOpenExternalUri(uri))
        {
            state.AddSystemMessage($"Could not open the default mail app for {details.AuthorEmail}.");
        }
    }

    private static void OpenGitCommitOnGitHub(AppState state, string? githubUrl)
    {
        if (string.IsNullOrWhiteSpace(githubUrl))
        {
            state.AddSystemMessage("Could not open GitHub: no GitHub origin remote was found.");
            return;
        }

        if (!TryOpenExternalUri(githubUrl))
        {
            state.AddSystemMessage("Could not open the commit on GitHub.");
        }
    }

    private static string? TryBuildGitHubCommitUrl(string root, string commitHash)
    {
        string? remoteUrl = RunGit(root, "remote", "get-url", "origin");
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        string normalized = remoteUrl.Trim();
        if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://github.com/" + normalized["git@github.com:".Length..];
        }
        else if (normalized.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
                 normalized.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            return null;
        }

        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return $"{normalized}/commit/{commitHash}";
    }

    private static void HandleGitSidebarClick(AppState state, int row)
    {
        if (state.ActiveModal is not null ||
            state.IsReaderViewActive ||
            state.IsCopyModeActive)
        {
            return;
        }

        int index = row - state.GitSidebarContentTopRow;
        GitSidebarLine[] lines = state.VisibleGitSidebarLines;
        if (index < 0 || index >= lines.Length)
        {
            return;
        }

        GitSidebarLine line = lines[index];
        int selectedIndex = state.GitSidebarScrollOffset + index;
        if (selectedIndex >= 0)
        {
            state.GitSidebarSelectedIndex = selectedIndex;
            EnsureGitSidebarSelectionVisible(state);
        }

        ActivateGitSidebarSelection(state);
    }

    private static void OpenFileInEditor(AppState state, string filePath)
    {
        if (!File.Exists(filePath))
        {
            state.AddSystemMessage($"File not found: {filePath}");
            return;
        }

        string quoted = $"\"{filePath}\"";

        if (OperatingSystem.IsWindows())
        {
            if (TryStartProcess("code", quoted, useShell: true) ||
                TryStartProcess(filePath, string.Empty, useShell: true))
            {
                return;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (TryStartProcess("code", quoted, useShell: false) ||
                TryStartProcess("open", quoted, useShell: false))
            {
                return;
            }
        }
        else
        {
            if (TryStartProcess("code", quoted, useShell: false) ||
                TryStartProcess("xdg-open", quoted, useShell: false))
            {
                return;
            }
        }

        state.AddSystemMessage($"Could not open {filePath}.");
    }

    private static bool TryStartProcess(string fileName, string arguments, bool useShell)
    {
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = useShell,
                CreateNoWindow = true
            };

            using Process? process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryOpenExternalUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryStartProcess(uri, string.Empty, useShell: true);
        }

        if (OperatingSystem.IsMacOS())
        {
            return TryStartProcess("open", $"\"{uri}\"", useShell: false);
        }

        return TryStartProcess("xdg-open", $"\"{uri}\"", useShell: false);
    }

    private sealed record GitCommitDetails(
        string FullHash,
        string Message,
        string AuthorName,
        string AuthorEmail,
        DateTimeOffset? Timestamp,
        int TotalAdditions,
        int TotalDeletions,
        IReadOnlyList<string> Files);

    private sealed record GitStashEntry(
        string Ref,
        string Description);
}
