using StemCode.Application.Models;
using System.Text;

namespace StemCode.Application.Commands;

internal static class PermissionCommandSupport
{
    public static string BuildPermissionsSummary(
        PermissionSettings settings,
        ReplSessionContext session)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);

        int configuredRuleCount = settings.Rules?.Length ?? 0;
        int sessionRuleCount = session.PermissionOverrides.Count;

        return
            "Permissions:\n" +
            $"Default mode: {ToDisplayText(settings.DefaultMode)}\n" +
            $"Auto approve all tools: {(settings.AutoApproveAllTools ? "On" : "Off")}\n" +
            $"Sandbox mode: {ToDisplayText(settings.SandboxMode)}\n" +
            $"Sandbox network: {DescribeSandboxNetwork(settings.SandboxNetwork)}\n" +
            $"Built-in/configured rules: {configuredRuleCount}\n" +
            $"Session overrides: {sessionRuleCount}\n" +
            "\n" +
            "Session overrides only affect the current REPL session.\n" +
            "Use /rules to inspect the full rule stack.\n" +
            "\n" +
            "Examples:\n" +
            "/allow edit src/**\n" +
            "/allow file_write src/App.js\n" +
            "/deny bash <command-pattern>\n" +
            "/deny apply_patch docs/**";
    }

    private static string DescribeSandboxNetwork(SandboxNetworkSettings? network)
    {
        return network?.Mode switch
        {
            SandboxNetworkMode.Blocked => "blocked",
            SandboxNetworkMode.Allowlist => network.AllowedDomains.Length == 0
                ? "allowlist (no domains allowed)"
                : $"allowlist ({string.Join(", ", network.AllowedDomains)})",
            _ => "unrestricted"
        };
    }

    public static string BuildRulesListing(
        PermissionSettings settings,
        ReplSessionContext session)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);

        StringBuilder builder = new();
        builder.AppendLine("Effective permission rules:");
        builder.Append("Default mode: ");
        builder.AppendLine(ToDisplayText(settings.DefaultMode));
        builder.Append("Auto approve all tools: ");
        builder.AppendLine(settings.AutoApproveAllTools ? "On" : "Off");
        builder.Append("Sandbox mode: ");
        builder.AppendLine(ToDisplayText(settings.SandboxMode));
        builder.AppendLine();
        builder.AppendLine("Built-in and configured rules:");
        AppendRuleBlock(builder, settings.Rules ?? []);
        builder.AppendLine();
        builder.AppendLine("Session overrides:");
        AppendRuleBlock(builder, session.PermissionOverrides);
        builder.AppendLine();
        builder.Append("Later rules win when multiple rules match the same tool and target.");
        return builder.ToString();
    }

    public static bool TryParseOverrideArguments(
        ReplCommandContext context,
        string usage,
        out string toolPattern,
        out string? subjectPattern,
        out ReplCommandResult? errorResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(usage);

        if (context.Arguments.Count == 0 ||
            string.IsNullOrWhiteSpace(context.Arguments[0]))
        {
            toolPattern = string.Empty;
            subjectPattern = null;
            errorResult = ReplCommandResult.Continue(
                $"Usage: {usage}",
                ReplFeedbackKind.Error);
            return false;
        }

        toolPattern = context.Arguments[0].Trim();
        string remainingArguments = context.ArgumentText.Length > toolPattern.Length
            ? context.ArgumentText[toolPattern.Length..].Trim()
            : string.Empty;

        subjectPattern = string.IsNullOrWhiteSpace(remainingArguments)
            ? null
            : remainingArguments;
        errorResult = null;
        return true;
    }

    public static ReplCommandResult AddSessionOverride(
        ReplSessionContext session,
        PermissionMode mode,
        string toolPattern,
        string? subjectPattern)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.AddPermissionOverride(new PermissionRule
        {
            Mode = mode,
            Tools = [toolPattern],
            Patterns = subjectPattern is null ? [] : [subjectPattern]
        });

        string modeText = ToDisplayText(mode).ToLowerInvariant();
        string message = subjectPattern is null
            ? $"Added a session {modeText} rule for '{toolPattern}' across all targets. Use /rules to review it."
            : $"Added a session {modeText} rule for '{toolPattern}' on '{subjectPattern}'. Use /rules to review it.";

        return ReplCommandResult.Continue(message, ReplFeedbackKind.Info);
    }

    public static string FormatRule(PermissionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        string tools = rule.Tools.Length == 0
            ? "*"
            : string.Join(", ", rule.Tools);
        string patterns = rule.Patterns.Length == 0
            ? "*"
            : string.Join(", ", rule.Patterns);

        return $"{ToDisplayText(rule.Mode)} | tools: {tools} | patterns: {patterns}";
    }

    private static void AppendRuleBlock(
        StringBuilder builder,
        IReadOnlyList<PermissionRule> rules)
    {
        if (rules.Count == 0)
        {
            builder.Append("(none)");
            return;
        }

        for (int index = 0; index < rules.Count; index++)
        {
            builder.Append(index + 1);
            builder.Append(". ");
            builder.AppendLine(FormatRule(rules[index]));
        }
    }

    private static string ToDisplayText(PermissionMode mode)
    {
        return mode switch
        {
            PermissionMode.Allow => "Allow",
            PermissionMode.Ask => "Ask",
            PermissionMode.Deny => "Deny",
            _ => mode.ToString()
        };
    }

    private static string ToDisplayText(ToolSandboxMode mode)
    {
        return mode switch
        {
            ToolSandboxMode.ReadOnly => "Read only",
            ToolSandboxMode.WorkspaceWrite => "Workspace write",
            ToolSandboxMode.DangerFullAccess => "Danger full access",
            _ => mode.ToString()
        };
    }
}
