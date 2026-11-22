using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Tools.Models;
using StemCode.Infrastructure.Secrets;
using StemCode.Infrastructure.Tools;
using StemCode.Infrastructure.WindowsSandbox;
using StemCode.Infrastructure.Workspaces;
using StemCode.Tests.Infrastructure.Secrets.TestDoubles;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StemCode.Tests.Infrastructure.Tools;

public sealed class SandboxNetworkSettingsTests
{
    [Theory]
    [InlineData("github.com", 443, true)]
    [InlineData("GitHub.com.", 443, true)]
    [InlineData("api.github.com", 443, false)]
    [InlineData("registry.npmjs.org", 443, true)]
    [InlineData("npmjs.org", 443, true)]
    [InlineData("evilnpmjs.org", 443, false)]
    [InlineData("pypi.org", 443, true)]
    [InlineData("pypi.org", 8080, false)]
    [InlineData("10.0.0.5", 80, false)]
    public void IsAllowed_Should_MatchExactWildcardAndPortEntries(string host, int port, bool expected)
    {
        SandboxNetworkSettings settings = new()
        {
            Mode = SandboxNetworkMode.Allowlist,
            AllowedDomains = ["github.com", "*.npmjs.org", "pypi.org:443"]
        };

        settings.IsAllowed(host, port).Should().Be(expected);
    }

    [Fact]
    public void Modes_Should_AllowEverythingOrNothing()
    {
        new SandboxNetworkSettings().IsAllowed("anything.example", 443).Should().BeTrue();
        new SandboxNetworkSettings { Mode = SandboxNetworkMode.Blocked, AllowedDomains = ["*"] }
            .IsAllowed("github.com", 443).Should().BeFalse();
        new SandboxNetworkSettings { Mode = SandboxNetworkMode.Allowlist, AllowedDomains = ["*"] }
            .IsAllowed("github.com", 443).Should().BeTrue();
    }
}

public sealed class SandboxNetworkPlannerTests : IDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("stemcode-net-plan").FullName;

    [Theory]
    [InlineData(null, true)]
    [InlineData(SandboxNetworkMode.Allowlist, true)]
    [InlineData(SandboxNetworkMode.Blocked, false)]
    public void Linux_Should_DropNetworkNamespaceSharing_OnlyWhenBlocked(SandboxNetworkMode? mode, bool sharesNetwork)
    {
        ShellCommandSandboxPlan plan = CreatePlan(
            ShellCommandSandboxPlatform.Linux,
            mode is null ? null : new ShellCommandSandboxNetwork(mode.Value, 4000));

        plan.Request.Arguments.Contains("--share-net").Should().Be(sharesNetwork);
        plan.Request.Arguments.Should().Contain("--unshare-all");
    }

    [Fact]
    public void MacOs_Should_DenyOutboundExceptProxy_ForAllowlist()
    {
        ShellCommandSandboxPlan plan = CreatePlan(
            ShellCommandSandboxPlatform.MacOs,
            new ShellCommandSandboxNetwork(SandboxNetworkMode.Allowlist, 41234));
        string profile = plan.Request.Arguments[1];

        profile.Should().Contain("(deny network-outbound)");
        profile.Should().Contain("(allow network-outbound (remote ip \"localhost:41234\"))");
    }

    [Fact]
    public void MacOs_Should_LeaveNetworkAlone_WhenUnrestricted()
    {
        string profile = CreatePlan(ShellCommandSandboxPlatform.MacOs, null).Request.Arguments[1];

        profile.Should().NotContain("network-outbound");
    }

    [Fact]
    public void MacOs_Should_NotAllowProxy_WhenBlocked()
    {
        string profile = CreatePlan(
            ShellCommandSandboxPlatform.MacOs,
            new ShellCommandSandboxNetwork(SandboxNetworkMode.Blocked, 41234)).Request.Arguments[1];

        profile.Should().Contain("(deny network-outbound)").And.NotContain("localhost:41234");
    }

    public void Dispose()
    {
        Directory.Delete(_workspaceRoot, recursive: true);
    }

    private ShellCommandSandboxPlan CreatePlan(ShellCommandSandboxPlatform platform, ShellCommandSandboxNetwork? network)
    {
        return ShellCommandSandboxPlanner.Create(
            new ProcessExecutionRequest("/bin/bash", ["-lc", "curl https://example.com"], WorkingDirectory: _workspaceRoot),
            ToolSandboxMode.WorkspaceWrite,
            _workspaceRoot,
            _workspaceRoot,
            WorkspaceRestrictedPathPolicy.Load(_workspaceRoot),
            platform,
            network);
    }
}

public sealed class SandboxNetworkProxyTests : IAsyncDisposable
{
    private readonly TcpListener _upstream = new(IPAddress.Loopback, 0);
    private readonly SandboxNetworkProxy _proxy;

    public SandboxNetworkProxyTests()
    {
        _upstream.Start();
        _ = Task.Run(ServeUpstreamAsync);
        _proxy = new SandboxNetworkProxy(() => new SandboxNetworkSettings
        {
            Mode = SandboxNetworkMode.Allowlist,
            AllowedDomains = ["127.0.0.1"]
        });
    }

    private int UpstreamPort => ((IPEndPoint)_upstream.LocalEndpoint).Port;

    [Fact]
    public async Task HttpRequest_Should_BeForwarded_ForAllowedHost()
    {
        using HttpClient client = CreateProxiedClient();

        string body = await client.GetStringAsync($"http://127.0.0.1:{UpstreamPort}/hello");

        body.Should().Be("upstream:/hello");
    }

    [Fact]
    public async Task Request_Should_Get403AndBeRecorded_ForBlockedHost()
    {
        long sequence = _proxy.CurrentSequence;
        using HttpClient client = CreateProxiedClient();

        HttpResponseMessage response = await client.GetAsync("http://blocked.example/data");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not in the sandbox network allowlist");
        _proxy.GetBlockedHostsSince(sequence).Should().Equal("blocked.example");
    }

    [Fact]
    public async Task Connect_Should_TunnelBytes_ForAllowedHost_AndRefuseOthers()
    {
        int port = _proxy.EnsureStarted();

        string allowed = await SendConnectAsync(port, $"127.0.0.1:{UpstreamPort}", "GET /tunnel HTTP/1.1\r\nHost: x\r\n\r\n");
        string refused = await SendConnectAsync(port, "evil.example:443", null);

        allowed.Should().StartWith("HTTP/1.1 200 Connection Established").And.Contain("upstream:/tunnel");
        refused.Should().StartWith("HTTP/1.1 403");
    }

    public async ValueTask DisposeAsync()
    {
        _upstream.Stop();
        await _proxy.DisposeAsync();
    }

    private HttpClient CreateProxiedClient()
    {
        int port = _proxy.EnsureStarted();
        return new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}") { BypassProxyOnLocal = false },
            UseProxy = true
        });
    }

    private static async Task<string> SendConnectAsync(int proxyPort, string authority, string? tunneledRequest)
    {
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, proxyPort);
        NetworkStream stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"));
        if (tunneledRequest is not null)
        {
            await Task.Delay(100);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(tunneledRequest));
        }

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        StringBuilder received = new();
        byte[] buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (tunneledRequest is null || received.ToString().Contains("upstream:"))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        return received.ToString();
    }

    // Minimal HTTP server that answers every request with "upstream:<path>".
    private async Task ServeUpstreamAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _upstream.AcceptTcpClientAsync();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    NetworkStream stream = client.GetStream();
                    using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    string? requestLine = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                    {
                    }

                    string path = requestLine?.Split(' ')[1] ?? "/";
                    byte[] body = Encoding.ASCII.GetBytes("upstream:" + path);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(body);
                }
            });
        }
    }
}

public sealed class SandboxNetworkShellTests : IAsyncDisposable
{
    private readonly string _workspaceRoot = Directory.CreateTempSubdirectory("stemcode-net-shell").FullName;
    private readonly SandboxNetworkProxy _proxy;
    private readonly PermissionSettings _settings = new()
    {
        SandboxMode = ToolSandboxMode.WorkspaceWrite,
        SandboxNetwork = new SandboxNetworkSettings { Mode = SandboxNetworkMode.Allowlist, AllowedDomains = ["github.com"] }
    };

    public SandboxNetworkShellTests()
    {
        _proxy = new SandboxNetworkProxy(_settings);
    }

    [Fact]
    public async Task ExecuteAsync_Should_PointSandboxedCommandsAtProxy_AndReportBlockedHosts()
    {
        RecordingRunner windowsRunner = new(this);
        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(6, string.Empty, "curl: (56) CONNECT tunnel failed"));
        ShellCommandService sut = new(
            new RecordingProcessRunner(processRunner, this),
            new FixedRoot(_workspaceRoot),
            _settings,
            windowsSandboxProcessRunner: windowsRunner,
            sandboxNetworkProxy: _proxy);

        ShellCommandExecutionResult result = await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("curl https://evil.example", "."),
            CancellationToken.None);

        IReadOnlyDictionary<string, string> environment = LastEnvironment!;
        int port = _proxy.EnsureStarted();
        environment["HTTPS_PROXY"].Should().Be($"http://127.0.0.1:{port}");
        environment["http_proxy"].Should().Be($"http://127.0.0.1:{port}");
        environment["NO_PROXY"].Should().Contain("127.0.0.1");
        environment["STEMCODE_SANDBOX_NETWORK"].Should().Be("allowlist");
        result.StandardError.Should().Contain("[StemCode sandbox] Network access was blocked for: evil.example");
    }

    [Fact]
    public async Task ExecuteAsync_Should_NotRestrictEscalatedCommands()
    {
        FakeProcessRunner processRunner = new();
        processRunner.EnqueueResult(new ProcessExecutionResult(0, "ok", string.Empty));
        ShellCommandService sut = new(
            processRunner,
            new FixedRoot(_workspaceRoot),
            _settings,
            sandboxNetworkProxy: _proxy);

        await sut.ExecuteAsync(
            new ShellCommandExecutionRequest("curl https://evil.example", ".", SandboxPermissions: ShellCommandSandboxPermissions.RequireEscalated),
            CancellationToken.None);

        processRunner.Requests.Single().EnvironmentVariables!.Should().NotContainKey("HTTPS_PROXY");
    }

    internal IReadOnlyDictionary<string, string>? LastEnvironment { get; set; }

    // Simulates the sandboxed command making a request through the proxy.
    internal async Task SimulateBlockedRequestAsync(IReadOnlyDictionary<string, string>? environment)
    {
        LastEnvironment = environment;
        if (environment is null || !environment.TryGetValue("HTTP_PROXY", out string? proxy))
        {
            return;
        }

        using HttpClient client = new(new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true });
        await client.GetAsync("http://evil.example/");
    }

    public async ValueTask DisposeAsync()
    {
        await _proxy.DisposeAsync();
        Directory.Delete(_workspaceRoot, recursive: true);
    }

    private sealed class FixedRoot(string root) : IWorkspaceRootProvider
    {
        public string GetWorkspaceRoot() => root;
    }

    private sealed class RecordingProcessRunner(FakeProcessRunner inner, SandboxNetworkShellTests owner) : IProcessRunner
    {
        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken)
        {
            await owner.SimulateBlockedRequestAsync(request.EnvironmentVariables);
            return await inner.RunAsync(request, cancellationToken);
        }
    }

    private sealed class RecordingRunner(SandboxNetworkShellTests owner) : IWindowsSandboxProcessRunner
    {
        public async Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, WindowsSandboxExecutionContext context, CancellationToken cancellationToken)
        {
            await owner.SimulateBlockedRequestAsync(request.EnvironmentVariables);
            return new ProcessExecutionResult(6, string.Empty, "curl: (56) CONNECT tunnel failed");
        }

        public Task<IBackgroundProcessHandle> StartBackgroundAsync(ProcessExecutionRequest request, WindowsSandboxExecutionContext context, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }
}
