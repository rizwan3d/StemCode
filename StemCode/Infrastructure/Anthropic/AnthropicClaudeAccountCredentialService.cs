using Microsoft.Extensions.Logging;
using StemCode.Application.Abstractions;
using StemCode.Infrastructure.Secrets;
using System.ComponentModel;
using System.Text.Json;

namespace StemCode.Infrastructure.Anthropic;

internal sealed class AnthropicClaudeAccountCredentialService :
    IAnthropicClaudeAccountAuthenticator,
    IAnthropicClaudeAccountCredentialService
{
    internal const string ClaudeCodeCliCredential = "claude-code-cli";

    private readonly IProcessRunner _processRunner;
    private readonly IStatusMessageWriter _statusMessageWriter;
    private readonly ILogger<AnthropicClaudeAccountCredentialService> _logger;

    public AnthropicClaudeAccountCredentialService(
        IProcessRunner processRunner,
        IStatusMessageWriter statusMessageWriter,
        ILogger<AnthropicClaudeAccountCredentialService> logger)
    {
        _processRunner = processRunner;
        _statusMessageWriter = statusMessageWriter;
        _logger = logger;
    }

    public async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        await _statusMessageWriter.ShowInfoAsync(
            "Checking the local Claude Code subscription sign-in.",
            cancellationToken);

        await EnsureSignedInAsync(cancellationToken);

        await _statusMessageWriter.ShowSuccessAsync(
            "Claude Code subscription sign-in detected.",
            cancellationToken);

        // This is an integration marker, not an OAuth token. Claude Code owns and
        // refreshes its credentials; StemCode never reads or stores them.
        return ClaudeCodeCliCredential;
    }

    public async Task<AnthropicClaudeAccountResolvedCredential> ResolveAsync(
        string storedCredentials,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                storedCredentials?.Trim(),
                ClaudeCodeCliCredential,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The saved Claude subscription setup uses an unsupported legacy credential. " +
                "Run onboarding again to connect the local Claude Code CLI.");
        }

        await EnsureSignedInAsync(cancellationToken);
        return new AnthropicClaudeAccountResolvedCredential(ClaudeCodeCliCredential);
    }

    private async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        ProcessExecutionResult result;
        try
        {
            result = await _processRunner.RunAsync(
                new ProcessExecutionRequest(
                    "claude",
                    ["auth", "status", "--json"],
                    MaxOutputCharacters: 32_768),
                cancellationToken);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                "Claude Code is not installed or is not available on PATH. Install Claude Code, " +
                "run 'claude auth login', and then run StemCode onboarding again.",
                exception);
        }

        if (result.ExitCode != 0)
        {
            string detail = FirstNonEmpty(result.StandardError, result.StandardOutput);
            throw new InvalidOperationException(
                "Claude Code could not verify its subscription sign-in. Run 'claude auth login' " +
                $"and try again.{FormatDetail(detail)}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement root = document.RootElement;
            bool loggedIn = root.TryGetProperty("loggedIn", out JsonElement loggedInElement) &&
                loggedInElement.ValueKind == JsonValueKind.True;
            string? authMethod = root.TryGetProperty("authMethod", out JsonElement authMethodElement) &&
                authMethodElement.ValueKind == JsonValueKind.String
                    ? authMethodElement.GetString()
                    : null;

            if (!loggedIn || !string.Equals(authMethod, "claude.ai", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Claude Code is not signed in with a Claude subscription. Run 'claude auth login', " +
                    "choose your Claude account, and try onboarding again.");
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Claude Code returned an invalid auth status response.");
            throw new InvalidOperationException(
                "Claude Code returned an invalid authentication status. Update Claude Code and try again.",
                exception);
        }
    }

    private static string FirstNonEmpty(string first, string second)
    {
        return !string.IsNullOrWhiteSpace(first) ? first.Trim() : second.Trim();
    }

    private static string FormatDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        const int maxLength = 300;
        string truncated = detail.Length <= maxLength
            ? detail
            : detail[..(maxLength - 3)] + "...";
        return $" Claude Code reported: {truncated}";
    }
}
