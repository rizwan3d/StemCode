using StemCode.Application.Backend;

namespace StemCode.CLI;

internal enum CliMode
{
    Acp,
    Interactive,
    SingleTurn
}

internal sealed record CliInvocation(
    CliMode Mode,
    BackendRuntimeArguments RuntimeArguments,
    string? ProviderAuthKey,
    string? Prompt,
    bool JsonOutput,
    bool NoOldReader,
    bool AutoApproveAllTools,
    bool ShowHelp,
    bool ShowVersion,
    bool ShowDoctor)
{
    public string[] BackendArgs => RuntimeArguments.RawArgs;

    /// <summary>
    /// Null when the session runs in the current checkout. Otherwise the session
    /// runs in a fresh git worktree; an empty string means "pick a name".
    /// </summary>
    public string? WorktreeName { get; init; }

    /// <summary>
    /// <c>--output-format stream-json</c>: write each progress event of a one-shot
    /// run as a JSON line, ending with the same result object <c>--json</c> prints.
    /// </summary>
    public bool StreamJsonOutput { get; init; }

    public static CliInvocation Help { get; } = new(
        CliMode.Interactive,
        BackendRuntimeArguments.Empty,
        ProviderAuthKey: null,
        Prompt: null,
        JsonOutput: false,
        NoOldReader: false,
        AutoApproveAllTools: false,
        ShowHelp: true,
        ShowVersion: false,
        ShowDoctor: false);

    public static CliInvocation Version { get; } = new(
        CliMode.Interactive,
        BackendRuntimeArguments.Empty,
        ProviderAuthKey: null,
        Prompt: null,
        JsonOutput: false,
        NoOldReader: false,
        AutoApproveAllTools: false,
        ShowHelp: false,
        ShowVersion: true,
        ShowDoctor: false);

    public static CliInvocation Parse(
        IReadOnlyList<string> args,
        bool stdinRedirected,
        Func<string> readStandardInput)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readStandardInput);

        List<string> remainingArgs = [];
        string? worktreeName = null;
        bool streamJson = false;
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                remainingArgs.AddRange(args.Skip(index));
                break;
            }

            if (TryReadOptionValue(args, ref index, "--output-format", out string? outputFormat))
            {
                switch (outputFormat!.ToLowerInvariant())
                {
                    case "text":
                        break;
                    case "json":
                        remainingArgs.Add("--json");
                        break;
                    case "stream-json":
                    case "stream_json":
                        streamJson = true;
                        break;
                    default:
                        throw new ArgumentException(
                            "Invalid value for --output-format. Expected one of: text, json, stream-json.");
                }

                continue;
            }

            if (string.Equals(arg, "--worktree", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-w", StringComparison.Ordinal))
            {
                worktreeName = string.Empty;
                continue;
            }

            if (arg.StartsWith("--worktree=", StringComparison.OrdinalIgnoreCase))
            {
                worktreeName = arg["--worktree=".Length..].Trim();
                if (worktreeName.Length == 0)
                {
                    throw new ArgumentException("Missing value for --worktree=.");
                }

                continue;
            }

            remainingArgs.Add(arg);
        }

        CliInvocation invocation = ParseCore(remainingArgs, stdinRedirected, readStandardInput);
        if (invocation.ShowHelp || invocation.ShowVersion)
        {
            return invocation;
        }

        if (streamJson)
        {
            if (invocation.Mode != CliMode.SingleTurn || invocation.ShowDoctor)
            {
                throw new ArgumentException("--output-format stream-json requires a one-shot prompt.");
            }

            if (invocation.JsonOutput)
            {
                throw new ArgumentException("--output-format stream-json cannot be combined with --json.");
            }

            invocation = invocation with { StreamJsonOutput = true };
        }

        return worktreeName is null
            ? invocation
            : invocation with { WorktreeName = worktreeName };
    }

    private static CliInvocation ParseCore(
        IReadOnlyList<string> args,
        bool stdinRedirected,
        Func<string> readStandardInput)
    {
        BackendRuntimeArguments.Builder runtimeArgumentsBuilder = new();
        List<string> promptParts = [];
        string? providerAuthKey = null;
        bool forceAcp = false;
        bool forceInteractive = false;
        bool jsonOutput = false;
        bool noOldReader = false;
        bool autoApproveAllTools = false;
        bool readPromptFromStandardInput = false;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];

            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                promptParts.AddRange(args.Skip(index + 1));
                break;
            }

            if (IsHelpOption(arg))
            {
                return Help;
            }

            if (IsVersionOption(arg))
            {
                return Version;
            }

            if (IsDoctorOption(arg))
            {
                return new CliInvocation(
                    CliMode.SingleTurn,
                    runtimeArgumentsBuilder.Build(),
                    providerAuthKey,
                    Prompt: "/doctor",
                    JsonOutput: false,
                    NoOldReader: false,
                    AutoApproveAllTools: autoApproveAllTools,
                    ShowHelp: false,
                    ShowVersion: false,
                    ShowDoctor: true);
            }

            if (IsAcpOption(arg))
            {
                forceAcp = true;
                continue;
            }

            if (IsInteractiveOption(arg))
            {
                forceInteractive = true;
                continue;
            }

            if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
                continue;
            }

            if (string.Equals(arg, "--no-old-reader", StringComparison.OrdinalIgnoreCase))
            {
                noOldReader = true;
                continue;
            }

            if (string.Equals(arg, "--yes", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-y", StringComparison.OrdinalIgnoreCase))
            {
                autoApproveAllTools = true;
                continue;
            }

            if (runtimeArgumentsBuilder.TryConsume(args, ref index))
            {
                continue;
            }

            if (TryConsumeProviderAuthKeyOption(args, ref index, out string? authKey))
            {
                providerAuthKey = authKey;
                continue;
            }

            if (TryConsumePromptOption(args, ref index, promptParts))
            {
                continue;
            }

            if (string.Equals(arg, "--stdin", StringComparison.OrdinalIgnoreCase))
            {
                readPromptFromStandardInput = true;
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown option '{arg}'.");
            }

            promptParts.Add(arg);
        }

        if (forceAcp)
        {
            if (forceInteractive)
            {
                throw new ArgumentException("--acp cannot be combined with --interactive.");
            }

            if (jsonOutput)
            {
                throw new ArgumentException("--json cannot be combined with --acp.");
            }

            if (readPromptFromStandardInput || promptParts.Count > 0)
            {
                throw new ArgumentException("--acp uses stdin for Agent Client Protocol messages and cannot accept a one-shot prompt.");
            }

            return new CliInvocation(
                CliMode.Acp,
                runtimeArgumentsBuilder.Build(),
                providerAuthKey,
                Prompt: null,
                JsonOutput: false,
                NoOldReader: noOldReader,
                AutoApproveAllTools: autoApproveAllTools,
                ShowHelp: false,
                ShowVersion: false,
                ShowDoctor: false);
        }

        if (forceInteractive)
        {
            if (jsonOutput)
            {
                throw new ArgumentException("--json requires a one-shot prompt.");
            }

            if (noOldReader &&
                string.IsNullOrWhiteSpace(runtimeArgumentsBuilder.SectionId))
            {
                throw new ArgumentException("--no-old-reader requires --session or --section in interactive mode.");
            }

            if (stdinRedirected)
            {
                throw new ArgumentException("--interactive requires terminal input.");
            }

            return new CliInvocation(
                CliMode.Interactive,
                runtimeArgumentsBuilder.Build(),
                providerAuthKey,
                Prompt: null,
                JsonOutput: false,
                NoOldReader: noOldReader,
                AutoApproveAllTools: autoApproveAllTools,
                ShowHelp: false,
                ShowVersion: false,
                ShowDoctor: false);
        }

        if (readPromptFromStandardInput)
        {
            if (!stdinRedirected)
            {
                throw new ArgumentException("--stdin requires redirected standard input.");
            }

            promptParts.Add(readStandardInput());
        }
        else if (promptParts.Count == 0 && stdinRedirected)
        {
            promptParts.Add(readStandardInput());
        }

        string prompt = string.Join(' ', promptParts).Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            if (noOldReader &&
                string.IsNullOrWhiteSpace(runtimeArgumentsBuilder.SectionId))
            {
                throw new ArgumentException("--no-old-reader requires --session or --section in interactive mode.");
            }

            if (jsonOutput)
            {
                throw new ArgumentException("--json requires a one-shot prompt.");
            }

            if (stdinRedirected)
            {
                throw new ArgumentException("No prompt was provided.");
            }

            return new CliInvocation(
                CliMode.Interactive,
                runtimeArgumentsBuilder.Build(),
                providerAuthKey,
                Prompt: null,
                JsonOutput: false,
                NoOldReader: noOldReader,
                AutoApproveAllTools: autoApproveAllTools,
                ShowHelp: false,
                ShowVersion: false,
                ShowDoctor: false);
        }

        if (noOldReader)
        {
            throw new ArgumentException("--no-old-reader requires --session or --section in interactive mode.");
        }

        return new CliInvocation(
            CliMode.SingleTurn,
            runtimeArgumentsBuilder.Build(),
            providerAuthKey,
            prompt,
            jsonOutput,
            NoOldReader: false,
            autoApproveAllTools,
            ShowHelp: false,
            ShowVersion: false,
            ShowDoctor: false);
    }

    private static bool IsHelpOption(string arg)
    {
        return string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionOption(string arg)
    {
        return string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDoctorOption(string arg)
    {
        return string.Equals(arg, "--doctor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAcpOption(string arg)
    {
        return string.Equals(arg, "--acp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInteractiveOption(string arg)
    {
        return string.Equals(arg, "--interactive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConsumePromptOption(
        IReadOnlyList<string> args,
        ref int index,
        List<string> promptParts)
    {
        string arg = args[index];

        if (TryReadOptionValue(args, ref index, "--prompt", out string? prompt) ||
            TryReadOptionValue(args, ref index, "-p", out prompt))
        {
            promptParts.Add(prompt!);
            return true;
        }

        return false;
    }

    private static bool TryConsumeProviderAuthKeyOption(
        IReadOnlyList<string> args,
        ref int index,
        out string? providerAuthKey)
    {
        return TryReadOptionValue(args, ref index, "--provider-auth-key", out providerAuthKey);
    }

    private static bool TryReadOptionValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        out string? value)
    {
        string arg = args[index];
        value = null;

        if (string.Equals(arg, optionName, StringComparison.OrdinalIgnoreCase))
        {
            int valueIndex = index + 1;
            if (valueIndex >= args.Count || string.IsNullOrWhiteSpace(args[valueIndex]))
            {
                throw new ArgumentException($"Missing value for {optionName}.");
            }

            value = args[valueIndex].Trim();
            index = valueIndex;
            return true;
        }

        string prefix = optionName + "=";
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        value = arg[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing value for {optionName}.");
        }

        return true;
    }
}
