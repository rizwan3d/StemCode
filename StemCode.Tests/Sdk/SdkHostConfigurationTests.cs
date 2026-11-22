using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StemCode.Application.Abstractions;
using StemCode.Application.Backend;
using StemCode.Application.Models;
using StemCode.Application.UI;
using StemCode.Domain.Services;
using StemCode.Sdk.Internal;

namespace StemCode.Tests.Sdk;

/// <summary>
/// Verifies the host-level extension point used by the SDK: the
/// <c>configureServices</c> callback runs after the built-in registrations so it
/// can override services (provider configuration, secret store, workspace root)
/// and contribute custom tools that the agent can discover.
/// </summary>
public sealed class SdkHostConfigurationTests
{
    [Fact]
    public async Task ConfigureServices_Should_RegisterCustomTool_And_OverrideStores()
    {
        string workspace = Directory.CreateTempSubdirectory("stemcode-sdk-test").FullName;

        try
        {
            AgentConfiguration configuration = new(
                new AgentProviderProfileFactory().CreateAnthropic(),
                PreferredModelId: null,
                ActiveProviderName: "Anthropic");

            void Configure(IServiceCollection services)
            {
                services.AddSingleton<IAgentConfigurationStore>(new InMemoryAgentConfigurationStore(configuration));
                services.AddSingleton<IApiKeySecretStore>(new InMemoryApiKeySecretStore("sk-test"));
                services.AddSingleton<IWorkspaceRootProvider>(new FixedWorkspaceRootProvider(workspace));
                services.AddSingleton<ITool>(new EchoTool());
            }

            using IHost host = StemCodeHostFactory.Create(
                new StubUiBridge(),
                BackendRuntimeArguments.Parse(["--no-update-check"]),
                [],
                autoApproveAllTools: true,
                Configure);

            IToolRegistry toolRegistry = host.Services.GetRequiredService<IToolRegistry>();
            toolRegistry.GetRegisteredToolNames().Should().Contain("sdk_echo");

            host.Services.GetRequiredService<IAgentConfigurationStore>()
                .Should().BeOfType<InMemoryAgentConfigurationStore>();

            IApiKeySecretStore secretStore = host.Services.GetRequiredService<IApiKeySecretStore>();
            (await secretStore.LoadAsync(CancellationToken.None)).Should().Be("sk-test");

            host.Services.GetRequiredService<IWorkspaceRootProvider>()
                .GetWorkspaceRoot().Should().Be(Path.GetFullPath(workspace));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Create_Should_AppendHeadlessToolRulesToPermissionSettings_And_RegisterOverrides()
    {
        using IHost host = StemCodeHostFactory.Create(
            new StubUiBridge(),
            BackendRuntimeArguments.Parse(
            [
                "--no-update-check",
                "--allowedTools", "shell_command(git *)",
                "--disallowedTools", "web_search"
            ]),
            [],
            autoApproveAllTools: false);

        PermissionSettings settings = host.Services.GetRequiredService<PermissionSettings>();
        settings.Rules.Should().HaveCountGreaterThanOrEqualTo(2);
        PermissionRule allow = settings.Rules[^2];
        PermissionRule deny = settings.Rules[^1];
        allow.Mode.Should().Be(PermissionMode.Allow);
        allow.Tools.Should().Equal("shell_command");
        allow.Patterns.Should().Equal("git *");
        deny.Mode.Should().Be(PermissionMode.Deny);
        deny.Tools.Should().Equal("web_search");

        host.Services.GetRequiredService<HeadlessRunOverrides>()
            .HiddenToolNames.Should().BeEquivalentTo(["web_search"]);
    }

    private sealed class EchoTool : ITool
    {
        public string Name => "sdk_echo";

        public string Description => "Echoes its input. Used for SDK registration tests.";

        public string PermissionRequirements => "{\"approvalMode\":\"Automatic\",\"toolTags\":[\"read\"]}";

        public string Schema => "{\"type\":\"object\",\"properties\":{}}";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolResult(ToolResultStatus.Success, "ok", "{}"));
        }
    }

    private sealed class StubUiBridge : IUiBridge
    {
        public Task<T> RequestSelectionAsync<T>(SelectionPromptRequest<T> request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<string> RequestTextAsync(TextPromptRequest request, bool isSecret, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public void ShowError(string message)
        {
        }

        public void ShowInfo(string message)
        {
        }

        public void ShowSuccess(string message)
        {
        }

        public void ShowAssistantReasoning(string reasoningText)
        {
        }

        public void ShowToolCalls(IReadOnlyList<ConversationToolCall> toolCalls)
        {
        }

        public void ShowToolResults(ToolExecutionBatchResult toolExecutionResult)
        {
        }

        public void ShowExecutionPlan(ExecutionPlanProgress progress)
        {
        }
    }
}
