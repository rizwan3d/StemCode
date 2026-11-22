namespace StemCode.Application.Abstractions;

/// <summary>OAuth sign-in for remote (HTTP) MCP servers.</summary>
public interface IMcpAuthService
{
    /// <summary>Runs the browser sign-in for the named server and reconnects MCP. Returns a status message.</summary>
    Task<string> LoginAsync(string serverName, CancellationToken cancellationToken);

    /// <summary>Forgets the stored tokens for the named server and reconnects MCP. Returns a status message.</summary>
    Task<string> LogoutAsync(string serverName, CancellationToken cancellationToken);
}
