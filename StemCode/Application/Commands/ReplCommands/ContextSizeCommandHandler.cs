using System.Globalization;
using StemCode.Application.Models;

namespace StemCode.Application.Commands;

internal sealed class ContextSizeCommandHandler : IReplCommandHandler
{
    public string CommandName => "context-size";

    public string Description => "Show, set, or clear the session context window cap.";

    public string Usage => "/context-size [show|auto|8k|32k|64k|125k|256k|<tokens>]";

    public Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string argument = context.ArgumentText.Trim();
        if (argument.Length == 0 ||
            string.Equals(argument, "show", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ReplCommandResult.Continue(FormatStatus(context.Session)));
        }

        if (string.Equals(argument, "auto", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "default", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "api", StringComparison.OrdinalIgnoreCase))
        {
            context.Session.SetContextWindowOverride(null);
            return Task.FromResult(ReplCommandResult.Continue(
                "Context size cap cleared. StemCode will use the provider-reported context window and existing fallback behavior."));
        }

        int requestedTokens;
        try
        {
            requestedTokens = ContextSizeOptions.Parse(argument);
        }
        catch (ArgumentException exception)
        {
            return Task.FromResult(ReplCommandResult.Continue(
                exception.Message,
                ReplFeedbackKind.Error));
        }

        context.Session.SetContextWindowOverride(requestedTokens);

        int? reportedTokens = context.Session.ActiveModelReportedContextWindowTokens;
        int? effectiveTokens = context.Session.ActiveModelContextWindowTokens;
        string requestedText = FormatTokens(requestedTokens);

        if (reportedTokens is > 0 && effectiveTokens is > 0 && effectiveTokens.Value < requestedTokens)
        {
            return Task.FromResult(ReplCommandResult.Continue(
                $"Context size cap requested at {requestedText}; the provider reports {FormatTokens(reportedTokens.Value)}, so the effective context window is {FormatTokens(effectiveTokens.Value)}."));
        }

        return Task.FromResult(ReplCommandResult.Continue(
            $"Context size cap set to {requestedText} for this session."));
    }

    private static string FormatStatus(ReplSessionContext session)
    {
        int? reportedTokens = session.ActiveModelReportedContextWindowTokens;
        int? overrideTokens = session.ContextWindowOverrideTokens;
        int? effectiveTokens = session.ActiveModelContextWindowTokens;

        string reported = reportedTokens is > 0
            ? FormatTokens(reportedTokens.Value)
            : "not reported";
        string requested = overrideTokens is > 0
            ? FormatTokens(overrideTokens.Value)
            : "auto";
        string effective = effectiveTokens is > 0
            ? FormatTokens(effectiveTokens.Value)
            : "existing fallback";

        return $"Context size: requested {requested}; provider {reported}; effective {effective}.";
    }

    private static string FormatTokens(int tokens)
    {
        return tokens.ToString("N0", CultureInfo.InvariantCulture) + " tokens";
    }
}
