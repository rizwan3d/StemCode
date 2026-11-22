using Microsoft.Extensions.Configuration;
using System.Net;

namespace StemCode.Application.Models;

public enum SandboxNetworkMode
{
    /// <summary>Sandboxed shell commands have normal network access (the default).</summary>
    Unrestricted = 0,

    /// <summary>Sandboxed shell commands may reach only <see cref="SandboxNetworkSettings.AllowedDomains"/>.</summary>
    Allowlist = 1,

    /// <summary>Sandboxed shell commands get no network access.</summary>
    Blocked = 2
}

/// <summary>
/// Network policy for sandboxed shell commands (<c>Application:Permissions:sandbox_network</c>).
/// Commands escalated to full access are not affected.
/// </summary>
public sealed class SandboxNetworkSettings
{
    public SandboxNetworkMode Mode { get; set; } = SandboxNetworkMode.Unrestricted;

    /// <summary>
    /// Hosts sandboxed commands may reach in <see cref="SandboxNetworkMode.Allowlist"/> mode.
    /// <c>example.com</c> matches only that host; <c>*.example.com</c> matches its
    /// subdomains (and the bare domain); an entry may pin a port as <c>host:port</c>.
    /// </summary>
    [ConfigurationKeyName("allowed_domains")]
    public string[] AllowedDomains { get; set; } = [];

    public bool IsRestricted => Mode != SandboxNetworkMode.Unrestricted;

    public bool IsAllowed(string host, int port)
    {
        if (Mode == SandboxNetworkMode.Unrestricted)
        {
            return true;
        }

        if (Mode == SandboxNetworkMode.Blocked || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        string normalizedHost = NormalizeHost(host);
        foreach (string rawEntry in AllowedDomains ?? [])
        {
            if (string.IsNullOrWhiteSpace(rawEntry))
            {
                continue;
            }

            (string pattern, int? entryPort) = SplitPort(rawEntry.Trim().ToLowerInvariant());
            if (entryPort is not null && entryPort != port)
            {
                continue;
            }

            if (pattern == "*")
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = pattern[2..];
                if (normalizedHost == suffix || normalizedHost.EndsWith("." + suffix, StringComparison.Ordinal))
                {
                    return true;
                }

                continue;
            }

            if (normalizedHost == NormalizeHost(pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeHost(string host)
    {
        string trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']')
            ? trimmed[1..^1]
            : trimmed;
    }

    private static (string Pattern, int? Port) SplitPort(string entry)
    {
        // IPv6 literals contain colons; only treat a trailing :digits as a port when
        // the rest is not itself an IP address.
        int colon = entry.LastIndexOf(':');
        if (colon <= 0 ||
            !int.TryParse(entry[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) ||
            (IPAddress.TryParse(entry, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6))
        {
            return (entry, null);
        }

        return (entry[..colon], port);
    }
}
