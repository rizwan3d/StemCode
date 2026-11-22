using Microsoft.Extensions.Logging;
using StemCode.Application.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StemCode.Infrastructure.Tools;

/// <summary>
/// Loopback HTTP proxy that sandboxed shell commands are pointed at (through
/// HTTP_PROXY / HTTPS_PROXY / ALL_PROXY) when <c>sandbox_network</c> is restricted.
/// It tunnels HTTPS (<c>CONNECT</c>) and forwards plain HTTP only for hosts the
/// policy allows, answers everything else with 403, and remembers what it blocked
/// so the shell tool can tell the agent.
/// </summary>
internal sealed class SandboxNetworkProxy : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBlockedRecords = 200;

    private readonly Func<SandboxNetworkSettings> _settings;
    private readonly ILogger<SandboxNetworkProxy>? _logger;
    private readonly object _startGate = new();
    private readonly ConcurrentQueue<(long Sequence, string Host)> _blocked = new();
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _sequence;

    public SandboxNetworkProxy(PermissionSettings permissionSettings, ILogger<SandboxNetworkProxy>? logger = null)
        : this(() => permissionSettings.SandboxNetwork ?? new SandboxNetworkSettings(), logger)
    {
    }

    internal SandboxNetworkProxy(Func<SandboxNetworkSettings> settings, ILogger<SandboxNetworkProxy>? logger = null)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Starts the proxy on first use and returns its loopback port.</summary>
    public int EnsureStarted()
    {
        lock (_startGate)
        {
            if (_listener is not null)
            {
                return ((IPEndPoint)_listener.LocalEndpoint).Port;
            }

            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _shutdown.Token));
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    /// <summary>A marker to pass to <see cref="GetBlockedHostsSince"/> later.</summary>
    public long CurrentSequence => Interlocked.Read(ref _sequence);

    public IReadOnlyList<string> GetBlockedHostsSince(long sequence)
    {
        return _blocked
            .Where(record => record.Sequence > sequence)
            .Select(static record => record.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream clientStream = client.GetStream();
                (byte[] head, byte[] leftover)? request = await ReadHeadAsync(clientStream, cancellationToken);
                if (request is null)
                {
                    return;
                }

                string headText = Encoding.Latin1.GetString(request.Value.head);
                string requestLine = headText[..headText.IndexOf("\r\n", StringComparison.Ordinal)];
                string[] parts = requestLine.Split(' ', 3);
                if (parts.Length < 3)
                {
                    await WriteErrorAsync(clientStream, 400, "Bad Request", "Malformed proxy request.", cancellationToken);
                    return;
                }

                if (string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConnectAsync(clientStream, parts[1], request.Value.leftover, cancellationToken);
                    return;
                }

                await HandleHttpAsync(clientStream, parts, headText, request.Value.leftover, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Connection dropped by either side; nothing to report.
            }
        }
    }

    private async Task HandleConnectAsync(
        NetworkStream clientStream,
        string authority,
        byte[] leftover,
        CancellationToken cancellationToken)
    {
        if (!TrySplitAuthority(authority, 443, out string host, out int port))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "Invalid CONNECT target.", cancellationToken);
            return;
        }

        if (!await AuthorizeAsync(clientStream, host, port, cancellationToken))
        {
            return;
        }

        using TcpClient upstream = new();
        try
        {
            await upstream.ConnectAsync(host, port, cancellationToken);
        }
        catch (SocketException exception)
        {
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"Could not reach {host}:{port}: {exception.Message}", cancellationToken);
            return;
        }

        byte[] established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n");
        await clientStream.WriteAsync(established, cancellationToken);
        NetworkStream upstreamStream = upstream.GetStream();
        if (leftover.Length > 0)
        {
            await upstreamStream.WriteAsync(leftover, cancellationToken);
        }

        await PipeAsync(clientStream, upstreamStream, cancellationToken);
    }

    private async Task HandleHttpAsync(
        NetworkStream clientStream,
        string[] requestLineParts,
        string headText,
        byte[] leftover,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(requestLineParts[1], UriKind.Absolute, out Uri? target) ||
            !string.Equals(target.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(clientStream, 400, "Bad Request", "The sandbox proxy only forwards absolute http:// URLs; use CONNECT for https.", cancellationToken);
            return;
        }

        if (!await AuthorizeAsync(clientStream, target.IdnHost, target.Port, cancellationToken))
        {
            return;
        }

        using TcpClient upstream = new();
        try
        {
            await upstream.ConnectAsync(target.IdnHost, target.Port, cancellationToken);
        }
        catch (SocketException exception)
        {
            await WriteErrorAsync(clientStream, 502, "Bad Gateway", $"Could not reach {target.Host}: {exception.Message}", cancellationToken);
            return;
        }

        // Rewrite to origin-form and force one request per connection so the
        // proxy never has to parse later requests on a reused connection.
        StringBuilder head = new();
        head.Append(requestLineParts[0]).Append(' ').Append(target.PathAndQuery).Append(' ').Append(requestLineParts[2]).Append("\r\n");
        foreach (string line in headText.Split("\r\n").Skip(1))
        {
            if (line.Length == 0 ||
                line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Keep-Alive:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            head.Append(line).Append("\r\n");
        }

        head.Append("Connection: close\r\n\r\n");
        NetworkStream upstreamStream = upstream.GetStream();
        await upstreamStream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancellationToken);
        if (leftover.Length > 0)
        {
            await upstreamStream.WriteAsync(leftover, cancellationToken);
        }

        await PipeAsync(clientStream, upstreamStream, cancellationToken);
    }

    private async Task<bool> AuthorizeAsync(
        NetworkStream clientStream,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        SandboxNetworkSettings settings = _settings();
        if (settings.IsAllowed(host, port))
        {
            return true;
        }

        RecordBlocked(port is 80 or 443 ? host : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}");
        _logger?.LogInformation("Sandbox network proxy blocked {Host}:{Port}.", host, port);
        string reason = settings.Mode == SandboxNetworkMode.Blocked
            ? "Network access is blocked for sandboxed commands."
            : $"{host} is not in the sandbox network allowlist (Application:Permissions:sandbox_network:allowed_domains).";
        await WriteErrorAsync(clientStream, 403, "Forbidden", $"Blocked by StemCode sandbox: {reason}", cancellationToken);
        return false;
    }

    private void RecordBlocked(string host)
    {
        long sequence = Interlocked.Increment(ref _sequence);
        _blocked.Enqueue((sequence, host));
        while (_blocked.Count > MaxBlockedRecords && _blocked.TryDequeue(out _))
        {
        }
    }

    private static async Task PipeAsync(NetworkStream client, NetworkStream upstream, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task clientToUpstream = CopyAndShutdownAsync(client, upstream, linked.Token);
        Task upstreamToClient = CopyAndShutdownAsync(upstream, client, linked.Token);
        await Task.WhenAny(clientToUpstream, upstreamToClient);
        // Give the other direction a moment to drain (e.g. the rest of a response).
        await Task.WhenAny(Task.WhenAll(clientToUpstream, upstreamToClient), Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
        linked.Cancel();
    }

    private static async Task CopyAndShutdownAsync(NetworkStream source, NetworkStream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, cancellationToken);
            destination.Socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task<(byte[] Head, byte[] Leftover)?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream received = new();
        while (received.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return null;
            }

            received.Write(buffer, 0, read);
            byte[] data = received.GetBuffer();
            int end = IndexOfHeaderEnd(data, (int)received.Length);
            if (end >= 0)
            {
                byte[] all = received.ToArray();
                return (all[..end], all[(end + 4)..]);
            }
        }

        return null;
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (int index = 0; index + 3 < length; index++)
        {
            if (data[index] == '\r' && data[index + 1] == '\n' && data[index + 2] == '\r' && data[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TrySplitAuthority(string authority, int defaultPort, out string host, out int port)
    {
        host = authority;
        port = defaultPort;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = authority[1..close];
            string rest = authority[(close + 1)..];
            return rest.Length == 0 ||
                   (rest.StartsWith(':') && int.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out port));
        }

        int colon = authority.LastIndexOf(':');
        if (colon > 0)
        {
            host = authority[..colon];
            return int.TryParse(authority[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
        }

        return host.Length > 0;
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int status, string reason, string message, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(message + "\n");
        string headers =
            $"HTTP/1.1 {status.ToString(CultureInfo.InvariantCulture)} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "X-StemCode-Sandbox: blocked\r\n" +
            $"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
