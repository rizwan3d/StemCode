using StemCode.Application.Models;
using StemCode.Application.Utilities;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.Workspaces;
using System.Text;

namespace StemCode.Infrastructure.Tools;

/// <summary>
/// Sandbox backend a plan targets. Passed explicitly so the plan for each backend can be
/// verified without running on that operating system.
/// </summary>
internal enum ShellCommandSandboxPlatform
{
    Unsupported,
    Linux,
    MacOs,
    Windows
}

internal sealed record ShellCommandSandboxPlan(
    ProcessExecutionRequest Request,
    string Enforcement,
    string? UnsupportedReason = null,
    IReadOnlyList<string>? RestrictedReadPaths = null)
{
    public bool IsUnsupported => !string.IsNullOrWhiteSpace(UnsupportedReason);

    /// <summary>
    /// Workspace paths that the OS sandbox blocks from being read, resolved from the workspace
    /// restriction policy.
    /// </summary>
    public IReadOnlyList<string> ResolvedRestrictedReadPaths => RestrictedReadPaths ?? [];
}

/// <summary>Network restriction for a sandboxed command and the filtering proxy's port, if any.</summary>
internal sealed record ShellCommandSandboxNetwork(SandboxNetworkMode Mode, int? ProxyPort);

internal static class ShellCommandSandboxPlanner
{
    public const string NoEnforcement = "none";
    public const string UnsupportedEnforcement = "unsupported";
    public const string BubblewrapEnforcement = "bubblewrap";
    public const string SandboxExecEnforcement = "sandbox-exec";
    public const string WindowsSandboxEnforcement = "windows-sandbox";

    public static ShellCommandSandboxPlan Create(
        ProcessExecutionRequest shellRequest,
        ToolSandboxMode effectiveSandboxMode,
        string workspaceRoot,
        string workingDirectory,
        WorkspaceRestrictedPathPolicy? restrictedPathPolicy = null,
        ShellCommandSandboxPlatform? platform = null,
        ShellCommandSandboxNetwork? network = null)
    {
        ArgumentNullException.ThrowIfNull(shellRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        if (effectiveSandboxMode == ToolSandboxMode.DangerFullAccess)
        {
            return new ShellCommandSandboxPlan(shellRequest, NoEnforcement);
        }

        string normalizedWorkspaceRoot = Path.GetFullPath(workspaceRoot);
        string normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        WorkspaceRestrictedPathPolicy policy = restrictedPathPolicy
            ?? WorkspaceRestrictedPathPolicy.LoadForSandbox(normalizedWorkspaceRoot);
        ShellCommandSandboxPlatform targetPlatform = platform ?? DetectPlatform();

        // A truncated policy means some restricted paths were never discovered, so the sandbox
        // rules would be incomplete. Fail closed rather than run a command under enforcement that
        // only looks complete.
        if (policy.Truncated)
        {
            throw new InvalidOperationException(
                "The workspace read restriction policy could not be fully resolved because the workspace exceeded the scan limits. " +
                "Narrow the patterns in .stemcode/.stemcodeignore (for example by ignoring large build or dependency directories) so the restricted paths can be enforced, " +
                "or rerun the command with sandbox_permissions 'require_escalated' to run without OS-level sandboxing.");
        }

        if (targetPlatform == ShellCommandSandboxPlatform.Linux)
        {
            return CreateLinuxPlan(
                shellRequest,
                effectiveSandboxMode,
                normalizedWorkspaceRoot,
                normalizedWorkingDirectory,
                policy,
                network);
        }

        if (targetPlatform == ShellCommandSandboxPlatform.MacOs)
        {
            return CreateMacOsPlan(
                shellRequest,
                effectiveSandboxMode,
                normalizedWorkspaceRoot,
                normalizedWorkingDirectory,
                policy,
                network);
        }

        if (targetPlatform == ShellCommandSandboxPlatform.Windows)
        {
            return new ShellCommandSandboxPlan(
                shellRequest,
                WindowsSandboxEnforcement,
                UnsupportedReason: null,
                RestrictedReadPaths: DescribeRestrictedPaths(policy));
        }

        return Unsupported(
            shellRequest,
            effectiveSandboxMode,
            "OS-level shell sandboxing is not available on this platform.");
    }

    private static ShellCommandSandboxPlatform DetectPlatform()
    {
        if (OperatingSystem.IsLinux())
        {
            return ShellCommandSandboxPlatform.Linux;
        }

        if (OperatingSystem.IsMacOS())
        {
            return ShellCommandSandboxPlatform.MacOs;
        }

        return OperatingSystem.IsWindows()
            ? ShellCommandSandboxPlatform.Windows
            : ShellCommandSandboxPlatform.Unsupported;
    }

    /// <summary>
    /// Builds a bubblewrap invocation that exposes only the system directories required to run
    /// commands plus the workspace, then masks every restricted workspace path.
    /// </summary>
    /// <remarks>
    /// The host root is deliberately never bound. bubblewrap applies mount operations in order,
    /// so the restriction masks are emitted after the workspace mount to guarantee they win.
    /// </remarks>
    private static ShellCommandSandboxPlan CreateLinuxPlan(
        ProcessExecutionRequest shellRequest,
        ToolSandboxMode effectiveSandboxMode,
        string workspaceRoot,
        string workingDirectory,
        WorkspaceRestrictedPathPolicy restrictedPathPolicy,
        ShellCommandSandboxNetwork? network = null)
    {
        List<string> arguments =
        [
            "--die-with-parent",
            "--unshare-all"
        ];

        // --unshare-all includes the network namespace; re-share it unless network
        // access is blocked outright. (An allowlist cannot reach a host-side proxy
        // from a private namespace, so it is enforced by the proxy instead.)
        if (network?.Mode != SandboxNetworkMode.Blocked)
        {
            arguments.Add("--share-net");
        }

        foreach (string systemRoot in SandboxHostPaths.LinuxSystemReadRoots)
        {
            AddLinuxReadOnlyMount(arguments, systemRoot, workspaceRoot);
        }

        foreach (string homePath in SandboxHostPaths.HomeReadPaths())
        {
            AddLinuxReadOnlyMount(arguments, homePath, workspaceRoot);
        }

        arguments.Add("--proc");
        arguments.Add("/proc");
        arguments.Add("--dev");
        arguments.Add("/dev");

        // Private, ephemeral temp directories. The host temp trees are never exposed because a
        // narrow root leaves them unmounted, and commands still need a writable temp location.
        AddLinuxWritableTempMount(arguments, "/tmp", workspaceRoot);
        AddLinuxWritableTempMount(arguments, "/var/tmp", workspaceRoot);

        arguments.Add(effectiveSandboxMode == ToolSandboxMode.WorkspaceWrite
            ? "--bind"
            : "--ro-bind");
        arguments.Add(workspaceRoot);
        arguments.Add(workspaceRoot);

        AddLinuxRestrictionMasks(arguments, restrictedPathPolicy, workspaceRoot);

        arguments.Add("--chdir");
        arguments.Add(workingDirectory);
        arguments.Add(shellRequest.FileName);
        arguments.AddRange(shellRequest.Arguments);

        return new ShellCommandSandboxPlan(
            shellRequest with
            {
                FileName = "bwrap",
                Arguments = arguments,
                WorkingDirectory = workspaceRoot
            },
            BubblewrapEnforcement,
            UnsupportedReason: null,
            RestrictedReadPaths: DescribeRestrictedPaths(restrictedPathPolicy));
    }

    private static void AddLinuxReadOnlyMount(
        List<string> arguments,
        string hostPath,
        string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return;
        }

        // The workspace gets its own mount later. Skip host paths inside the workspace so a
        // read-only system mount can never shadow it.
        if (IsSamePathOrDescendantSafe(workspaceRoot, hostPath))
        {
            return;
        }

        // `--ro-bind-try` tolerates sources that are absent on this host, which keeps the
        // argument list stable across distributions.
        arguments.Add("--ro-bind-try");
        arguments.Add(hostPath);
        arguments.Add(hostPath);
    }

    private static void AddLinuxWritableTempMount(
        List<string> arguments,
        string tempRoot,
        string workspaceRoot)
    {
        if (IsSamePathOrDescendantSafe(workspaceRoot, tempRoot))
        {
            return;
        }

        arguments.Add("--tmpfs");
        arguments.Add(tempRoot);
    }

    /// <summary>
    /// Masks restricted workspace paths inside the sandbox. Directories become empty tmpfs
    /// mounts and files are replaced by <c>/dev/null</c>, so a restricted path is unreadable
    /// even for a command that bypasses StemCode's own path checks.
    /// </summary>
    private static void AddLinuxRestrictionMasks(
        List<string> arguments,
        WorkspaceRestrictedPathPolicy restrictedPathPolicy,
        string workspaceRoot)
    {
        foreach (WorkspaceRestrictedPath restricted in restrictedPathPolicy.RestrictedPaths)
        {
            if (!IsSamePathOrDescendantSafe(workspaceRoot, restricted.FullPath))
            {
                continue;
            }

            if (WorkspacePath.PathEquals(workspaceRoot, restricted.FullPath))
            {
                // Masking the workspace root would make the command unusable and is never the
                // intent of an ignore rule.
                continue;
            }

            if (restricted.IsDirectory)
            {
                arguments.Add("--tmpfs");
                arguments.Add(restricted.FullPath);
                continue;
            }

            arguments.Add("--ro-bind");
            arguments.Add("/dev/null");
            arguments.Add(restricted.FullPath);
        }
    }

    private static ShellCommandSandboxPlan CreateMacOsPlan(
        ProcessExecutionRequest shellRequest,
        ToolSandboxMode effectiveSandboxMode,
        string workspaceRoot,
        string workingDirectory,
        WorkspaceRestrictedPathPolicy restrictedPathPolicy,
        ShellCommandSandboxNetwork? network = null)
    {
        string profile = BuildMacOsSandboxProfile(
            effectiveSandboxMode,
            workspaceRoot,
            restrictedPathPolicy,
            network);
        List<string> arguments = ["-p", profile, shellRequest.FileName];
        arguments.AddRange(shellRequest.Arguments);

        return new ShellCommandSandboxPlan(
            shellRequest with
            {
                FileName = "sandbox-exec",
                Arguments = arguments,
                WorkingDirectory = workingDirectory
            },
            SandboxExecEnforcement,
            UnsupportedReason: null,
            RestrictedReadPaths: DescribeRestrictedPaths(restrictedPathPolicy));
    }

    private static string BuildMacOsSandboxProfile(
        ToolSandboxMode effectiveSandboxMode,
        string workspaceRoot,
        WorkspaceRestrictedPathPolicy restrictedPathPolicy,
        ShellCommandSandboxNetwork? network = null)
    {
        StringBuilder builder = new();
        builder.AppendLine("(version 1)");
        builder.AppendLine("(allow default)");
        builder.AppendLine("(deny file-write*)");

        if (network is not null && network.Mode != SandboxNetworkMode.Unrestricted)
        {
            // Outbound traffic may only go to Unix sockets and, for an allowlist, the
            // StemCode filtering proxy; everything else is denied by the kernel.
            builder.AppendLine("(deny network-outbound)");
            builder.AppendLine("(allow network-outbound (remote unix-socket))");
            if (network.Mode == SandboxNetworkMode.Allowlist && network.ProxyPort is int proxyPort)
            {
                builder.Append("(allow network-outbound (remote ip \"localhost:");
                builder.Append(proxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
                builder.AppendLine("\"))");
            }
        }

        if (effectiveSandboxMode == ToolSandboxMode.WorkspaceWrite)
        {
            string[] writableRoots =
            [
                workspaceRoot,
                Path.GetTempPath(),
                "/tmp",
                "/private/tmp",
                "/var/tmp"
            ];

            builder.AppendLine("(allow file-write*");
            foreach (string writableRoot in writableRoots
                         .Where(static root => !string.IsNullOrWhiteSpace(root))
                         .Select(static root => Path.GetFullPath(root))
                         .Distinct(StringComparer.Ordinal))
            {
                builder.Append("  (subpath ");
                builder.Append(ToSandboxString(TrimTrailingSeparators(writableRoot)));
                builder.AppendLine(")");
            }

            builder.AppendLine(")");
        }

        AppendMacOsRestrictionDenyRules(builder, restrictedPathPolicy, workspaceRoot);
        return builder.ToString();
    }

    /// <summary>
    /// Emits explicit <c>file-read*</c> and <c>file-write*</c> deny rules for restricted paths.
    /// </summary>
    /// <remarks>
    /// Seatbelt profiles are last-match-wins, so these rules are appended after the
    /// <c>(allow default)</c> and writable-root rules to guarantee the deny takes effect.
    /// </remarks>
    private static void AppendMacOsRestrictionDenyRules(
        StringBuilder builder,
        WorkspaceRestrictedPathPolicy restrictedPathPolicy,
        string workspaceRoot)
    {
        List<string> directoryFilters = [];
        List<string> fileFilters = [];

        foreach (WorkspaceRestrictedPath restricted in restrictedPathPolicy.RestrictedPaths)
        {
            if (!IsSamePathOrDescendantSafe(workspaceRoot, restricted.FullPath) ||
                WorkspacePath.PathEquals(workspaceRoot, restricted.FullPath))
            {
                continue;
            }

            List<string> target = restricted.IsDirectory ? directoryFilters : fileFilters;
            foreach (string candidate in ExpandMacOsPathVariants(restricted.FullPath))
            {
                string filter = restricted.IsDirectory
                    ? $"(subpath {ToSandboxString(candidate)})"
                    : $"(literal {ToSandboxString(candidate)})";
                if (!target.Contains(filter, StringComparer.Ordinal))
                {
                    target.Add(filter);
                }
            }
        }

        if (directoryFilters.Count == 0 && fileFilters.Count == 0)
        {
            return;
        }

        foreach (string operation in new[] { "file-read*", "file-write*" })
        {
            builder.Append("(deny ");
            builder.AppendLine(operation);
            foreach (string filter in directoryFilters.Concat(fileFilters))
            {
                builder.Append("  ");
                builder.AppendLine(filter);
            }

            builder.AppendLine(")");
        }
    }

    /// <summary>
    /// macOS resolves symlinks before applying path filters, and <c>/tmp</c> and <c>/var</c> are
    /// symlinks into <c>/private</c>. Emitting both spellings keeps the deny effective wherever
    /// the workspace lives.
    /// </summary>
    private static IEnumerable<string> ExpandMacOsPathVariants(string fullPath)
    {
        List<string> variants = [];

        void addVariant(string candidate)
        {
            string normalized = TrimTrailingSeparators(candidate);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !variants.Contains(normalized, StringComparer.Ordinal))
            {
                variants.Add(normalized);
            }
        }

        void addWithPrivatePrefix(string candidate)
        {
            string normalized = TrimTrailingSeparators(candidate);
            addVariant(normalized);
            foreach (string prefix in new[] { "/tmp", "/var" })
            {
                if (normalized.Equals(prefix, StringComparison.Ordinal) ||
                    normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
                {
                    addVariant("/private" + normalized);
                    return;
                }
            }
        }

        addWithPrivatePrefix(fullPath);

        // Seatbelt matches on the resolved path, so deny the real target as well when the
        // restricted path (or one of its parents) is a symlink.
        addWithPrivatePrefix(WorkspaceRestrictedPathPolicy.ResolveRealPath(fullPath));

        return variants;
    }

    private static IReadOnlyList<string> DescribeRestrictedPaths(
        WorkspaceRestrictedPathPolicy restrictedPathPolicy)
    {
        return [.. restrictedPathPolicy.RestrictedPaths.Select(static restricted => restricted.RelativePath)];
    }

    private static bool IsSamePathOrDescendantSafe(string parentPath, string candidatePath)
    {
        try
        {
            return WorkspacePath.IsSamePathOrDescendant(parentPath, candidatePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                PathTooLongException or
                IOException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(trimmed) ? path : trimmed;
    }

    private static ShellCommandSandboxPlan Unsupported(
        ProcessExecutionRequest shellRequest,
        ToolSandboxMode effectiveSandboxMode,
        string reason)
    {
        return new ShellCommandSandboxPlan(
            shellRequest,
            UnsupportedEnforcement,
            $"{reason} The effective sandbox mode is '{ToWireValue(effectiveSandboxMode)}'. The command will run after StemCode permission approval without OS-level sandbox enforcement.");
    }

    private static string ToSandboxString(string value)
    {
        return "\"" +
               value
                   .Replace("\\", "\\\\", StringComparison.Ordinal)
                   .Replace("\"", "\\\"", StringComparison.Ordinal) +
               "\"";
    }

    private static string ToWireValue(ToolSandboxMode sandboxMode)
    {
        return sandboxMode switch
        {
            ToolSandboxMode.ReadOnly => "read-only",
            ToolSandboxMode.DangerFullAccess => "danger-full-access",
            _ => "workspace-write"
        };
    }
}
