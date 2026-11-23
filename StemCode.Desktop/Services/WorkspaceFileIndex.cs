namespace StemCode.Desktop.Services;

/// <summary>
/// Cached list of workspace-relative file paths for @-mention completion.
/// Skips build output and dependency folders; refreshed at most every 30 seconds.
/// </summary>
public sealed class WorkspaceFileIndex
{
    private const int MaxFiles = 25_000;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode-test", "node_modules", "bin", "obj",
        "dist", "build", "out", "target", ".next", ".nuxt", ".gradle", "__pycache__", ".venv",
        "venv", ".tox", ".nuget-packages", "packages", ".cache", "coverage", ".tmp"
    };

    private readonly object _sync = new();
    private string? _root;
    private string[] _files = [];
    private DateTimeOffset _builtAt;
    private Task? _buildTask;

    public IReadOnlyList<string> Search(string root, string query, int limit)
    {
        EnsureFresh(root);

        string[] files;
        lock (_sync)
        {
            files = _files;
        }

        string normalizedQuery = query.Replace('\\', '/').Trim();
        if (normalizedQuery.Length == 0)
        {
            return files.Take(limit).ToArray();
        }

        return files
            .Select(path => (path, score: Score(path, normalizedQuery)))
            .Where(static candidate => candidate.score > 0)
            .OrderByDescending(static candidate => candidate.score)
            .ThenBy(static candidate => candidate.path.Length)
            .Take(limit)
            .Select(static candidate => candidate.path)
            .ToArray();
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _builtAt = DateTimeOffset.MinValue;
        }
    }

    private void EnsureFresh(string root)
    {
        Task? firstBuild = null;
        lock (_sync)
        {
            bool sameRoot = string.Equals(_root, root, StringComparison.OrdinalIgnoreCase);
            bool fresh = sameRoot && DateTimeOffset.UtcNow - _builtAt < RefreshInterval;
            if (fresh || (_buildTask is { IsCompleted: false } && sameRoot))
            {
                return;
            }

            if (!sameRoot)
            {
                _files = [];
            }

            _root = root;
            _builtAt = DateTimeOffset.UtcNow;
            _buildTask = Task.Run(() =>
            {
                string[] built = Enumerate(root);
                lock (_sync)
                {
                    if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase))
                    {
                        _files = built;
                    }
                }
            });

            if (!sameRoot)
            {
                firstBuild = _buildTask;
            }
        }

        // First use for this root: wait briefly so the first popup has results.
        firstBuild?.Wait(TimeSpan.FromMilliseconds(400));
    }

    private static int Score(string path, string query)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 400;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 300;
        }

        if (path.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 250;
        }

        if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 200;
        }

        // Subsequence match ("mwvm" -> "MainWindowViewModel").
        int queryIndex = 0;
        foreach (char character in fileName)
        {
            if (queryIndex < query.Length && char.ToLowerInvariant(character) == char.ToLowerInvariant(query[queryIndex]))
            {
                queryIndex++;
            }
        }

        return queryIndex == query.Length ? 100 : 0;
    }

    private static string[] Enumerate(string root)
    {
        List<string> files = [];
        Stack<string> pending = new();
        pending.Push(root);

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                    if (files.Count >= MaxFiles)
                    {
                        break;
                    }
                }

                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    string name = Path.GetFileName(child);
                    if (!SkippedDirectories.Contains(name))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Unreadable folders are skipped.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files.ToArray();
    }
}
