using StemCode.Application.Models;

namespace StemCode.Application.Backend;

internal sealed record BackendRuntimeArguments(
    string[] RawArgs,
    string? SectionId,
    string? ProfileName,
    string? ThinkingMode,
    int? ContextWindowTokens,
    string? AppSurface,
    bool SkipUpdateCheck)
{
    public static BackendRuntimeArguments Empty { get; } = new(
        [],
        SectionId: null,
        ProfileName: null,
        ThinkingMode: null,
        ContextWindowTokens: null,
        AppSurface: null,
        SkipUpdateCheck: false);

    public string EffectiveAppSurface(string defaultAppSurface)
    {
        return string.IsNullOrWhiteSpace(AppSurface)
            ? BackendRuntimeOptions.NormalizeAppSurface(defaultAppSurface)
            : AppSurface;
    }

    public BackendRuntimeArguments WithDefaults(
        string defaultAppSurface,
        bool skipUpdateCheck = false)
    {
        return this with
        {
            AppSurface = EffectiveAppSurface(defaultAppSurface),
            SkipUpdateCheck = SkipUpdateCheck || skipUpdateCheck
        };
    }

    public static BackendRuntimeArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Builder builder = new(includePassthroughArgs: true);
        for (int index = 0; index < args.Count; index++)
        {
            if (builder.TryConsume(args, ref index))
            {
                continue;
            }

            builder.AddPassthrough(args[index]);
        }

        return builder.Build();
    }

    internal sealed class Builder
    {
        private const string SandboxModeConfigKey = "--Application:Permissions:SandboxMode";
        private readonly bool _includePassthroughArgs;
        private readonly List<string> _rawArgs = [];
        private string? _appSurface;

        public Builder(bool includePassthroughArgs = false)
        {
            _includePassthroughArgs = includePassthroughArgs;
        }

        public string? SectionId { get; private set; }

        public string? ProfileName { get; private set; }

        public string? ThinkingMode { get; private set; }

        public int? ContextWindowTokens { get; private set; }

        public string? AppSurface => _appSurface;

        public bool SkipUpdateCheck { get; private set; }

        public void AddPassthrough(string arg)
        {
            if (_includePassthroughArgs)
            {
                _rawArgs.Add(arg);
            }
        }

        public BackendRuntimeArguments Build()
        {
            return new BackendRuntimeArguments(
                _rawArgs.ToArray(),
                SectionId,
                ProfileName,
                ThinkingMode,
                ContextWindowTokens,
                _appSurface,
                SkipUpdateCheck);
        }

        public bool TryConsume(
            IReadOnlyList<string> args,
            ref int index)
        {
            if (TryConsumeFlag(args, ref index, "--no-update-check"))
            {
                SkipUpdateCheck = true;
                return true;
            }

            if (TryConsumeOption(args, ref index, "--section", out string? sectionId) ||
                TryConsumeOption(args, ref index, "--session", out sectionId))
            {
                SectionId = sectionId;
                return true;
            }

            if (TryConsumeOption(args, ref index, "--profile", out string? profileName))
            {
                ProfileName = profileName;
                return true;
            }

            if (TryConsumeOption(args, ref index, "--thinking", out string? thinkingMode))
            {
                ThinkingMode = thinkingMode;
                return true;
            }

            if (TryConsumeOption(args, ref index, "--context-size", out string? contextSize))
            {
                ContextWindowTokens = ContextSizeOptions.Parse(contextSize!);
                return true;
            }

            if (TryConsumeOption(args, ref index, "--surface", out string? appSurface))
            {
                _appSurface ??= BackendRuntimeOptions.NormalizeAppSurface(appSurface);
                return true;
            }

            if (TryReadOptionValue(args, ref index, "--sandbox-mode", out string? sandboxMode))
            {
                _rawArgs.Add($"{SandboxModeConfigKey}={NormalizeSandboxMode(sandboxMode)}");
                return true;
            }

            return false;
        }

        private static string NormalizeSandboxMode(string? value)
        {
            string normalized = value?.Trim() ?? string.Empty;
            return normalized.ToLowerInvariant() switch
            {
                "read-only" or "readonly" => "ReadOnly",
                "workspace-write" or "workspacewrite" => "WorkspaceWrite",
                "danger-full-access" or "dangerfullaccess" => "DangerFullAccess",
                _ => throw new ArgumentException(
                    "Invalid value for --sandbox-mode. Expected one of: read-only, workspace-write, danger-full-access.")
            };
        }

        private bool TryConsumeFlag(
            IReadOnlyList<string> args,
            ref int index,
            string optionName)
        {
            if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _rawArgs.Add(args[index]);
            return true;
        }

        private bool TryConsumeOption(
            IReadOnlyList<string> args,
            ref int index,
            string optionName,
            out string? value)
        {
            int originalIndex = index;
            if (!TryReadOptionValue(args, ref index, optionName, out value))
            {
                return false;
            }

            AddConsumedArgs(args, originalIndex, index);
            return true;
        }

        private void AddConsumedArgs(
            IReadOnlyList<string> args,
            int startIndex,
            int endIndex)
        {
            for (int tokenIndex = startIndex; tokenIndex <= endIndex; tokenIndex++)
            {
                _rawArgs.Add(args[tokenIndex]);
            }
        }
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
