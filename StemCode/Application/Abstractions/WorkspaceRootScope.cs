namespace StemCode.Application.Abstractions;

/// <summary>
/// Ambient, async-flow-scoped override for the workspace root. While a scope is
/// active, every <see cref="IWorkspaceRootProvider"/> reports the scoped root, so
/// file tools, the shell, permissions and hooks all operate inside it. Subagents
/// that run in an isolated git worktree use this to keep their edits out of the
/// parent workspace. The override follows the logical call (including awaited and
/// parallel child tasks) and never leaks back to the caller.
/// </summary>
public static class WorkspaceRootScope
{
    private static readonly AsyncLocal<string?> CurrentRoot = new();

    public static string? Current => CurrentRoot.Value;

    public static IDisposable Enter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string? previous = CurrentRoot.Value;
        CurrentRoot.Value = Path.GetFullPath(workspaceRoot.Trim());
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CurrentRoot.Value = _previous;
        }
    }
}
