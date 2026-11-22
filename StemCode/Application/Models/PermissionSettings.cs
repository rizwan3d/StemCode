using Microsoft.Extensions.Configuration;

namespace StemCode.Application.Models;

public sealed class PermissionSettings
{
    [ConfigurationKeyName("auto_approve_all_tools")]
    public bool AutoApproveAllTools { get; set; }

    public PermissionMode DefaultMode { get; set; } = PermissionMode.Ask;

    public PermissionRule[] Rules { get; set; } = [];

    public ToolSandboxMode SandboxMode { get; set; } = ToolSandboxMode.WorkspaceWrite;

    [ConfigurationKeyName("file_read")]
    public PermissionMode? FileRead { get; set; }

    [ConfigurationKeyName("file_write")]
    public PermissionMode? FileWrite { get; set; }

    [ConfigurationKeyName("file_delete")]
    public PermissionMode? FileDelete { get; set; }

    [ConfigurationKeyName("shell_default")]
    public PermissionMode? ShellDefault { get; set; }

    [ConfigurationKeyName("shell_safe")]
    public PermissionMode? ShellSafe { get; set; }

    [ConfigurationKeyName("network")]
    public PermissionMode? Network { get; set; }

    /// <summary>Network policy for sandboxed shell commands: unrestricted, allowlist, or blocked.</summary>
    [ConfigurationKeyName("sandbox_network")]
    public SandboxNetworkSettings SandboxNetwork { get; set; } = new();

    [ConfigurationKeyName("memory_write")]
    public PermissionMode? MemoryWrite { get; set; }

    [ConfigurationKeyName("mcp_tools")]
    public PermissionMode? McpTools { get; set; }

    [ConfigurationKeyName("shell")]
    public ShellPermissionSettings Shell { get; set; } = new();
}

public sealed class ShellPermissionSettings
{
    [ConfigurationKeyName("allow")]
    public ShellCommandPermissionSettings Allow { get; set; } = new();

    [ConfigurationKeyName("deny")]
    public ShellCommandPermissionSettings Deny { get; set; } = new();
}

public sealed class ShellCommandPermissionSettings
{
    [ConfigurationKeyName("commands")]
    public string[] Commands { get; set; } = [];
}
