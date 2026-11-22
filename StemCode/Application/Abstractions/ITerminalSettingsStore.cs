namespace StemCode.Application.Abstractions;

/// <summary>
/// Personal terminal preferences from <c>Application:Terminal</c> in the user's
/// and the workspace's <c>agent-profile.json</c> (workspace values win).
/// </summary>
public sealed class TerminalSettings
{
    /// <summary>Active output style name; null or "default" means none.</summary>
    public string? OutputStyle { get; set; }

    /// <summary>vi-style modal editing in the terminal prompt.</summary>
    public bool VimMode { get; set; }

    /// <summary>
    /// Shell command whose first output line replaces the built-in status line. It
    /// receives session details as JSON on standard input.
    /// </summary>
    public string? StatusLineCommand { get; set; }

    /// <summary>How often the status line command is re-run while idle, in seconds.</summary>
    public int StatusLineRefreshSeconds { get; set; } = 10;
}

public interface ITerminalSettingsStore
{
    TerminalSettings Load();

    /// <summary>Saves the output style to the user-level configuration (null clears it).</summary>
    Task SaveOutputStyleAsync(string? outputStyle, CancellationToken cancellationToken);

    /// <summary>Saves the vim mode preference to the user-level configuration.</summary>
    Task SaveVimModeAsync(bool enabled, CancellationToken cancellationToken);
}
