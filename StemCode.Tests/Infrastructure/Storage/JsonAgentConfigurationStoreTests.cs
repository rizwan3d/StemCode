using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Domain.Models;
using StemCode.Infrastructure.Storage;

namespace StemCode.Tests.Infrastructure.Storage;

public sealed class JsonAgentConfigurationStoreTests : IDisposable
{
    private readonly IReadOnlyList<EnvironmentVariableScope> _environmentVariables;
    private readonly string _tempRoot;

    public JsonAgentConfigurationStoreTests()
    {
        _environmentVariables =
        [
            new("STEMCODE_BASE_URL", null),
            new("STEMCODE_MODEL", null),
            new("STEMCODE_PROVIDER", null),
            new("STEMCODE_THINKING", null),
            new("STEMCODE_REASONING", null)
        ];

        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"StemCode-Config-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripGoogleAiStudioConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.GoogleAiStudio, null),
            "gemini-2.5-flash",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripAnthropicConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.Anthropic, null),
            "claude-sonnet-4-6",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripAnthropicClaudeAccountConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.AnthropicClaudeAccount, null),
            "claude-sonnet-4-6",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripGitHubCopilotConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.GitHubCopilot, null),
            "gpt-5",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripOpenRouterConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OpenRouter, null),
            "openai/gpt-4o",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripKiloCodeConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.KiloCode, null),
            "kilo-auto/free",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripOllamaConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            "llama3.2",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripLmStudioConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.LmStudio, "http://127.0.0.1:4321/v1"),
            "qwen3-8b",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripOllamaCloudConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OllamaCloud, null),
            "gpt-oss:20b",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripCerebrasConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.Cerebras, null),
            "llama3.1-8b",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripGroqConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.Groq, null),
            "llama-3.3-70b-versatile",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_Should_RoundTripOpenCodeZenConfiguration()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
            "qwen3.6-plus",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_Should_PreserveMultipleNamedProvidersAndSwitchActiveProvider()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        await sut.SaveAsync(
            new AgentConfiguration(
                new AgentProviderProfile(ProviderKind.OpenAi, null),
                "gpt-5.4",
                ReasoningEffort: null,
                ActiveProviderName: "OpenAI",
                ThinkingMode: "on"),
            CancellationToken.None);
        await sut.SaveAsync(
            new AgentConfiguration(
                new AgentProviderProfile(ProviderKind.Anthropic, null),
                "claude-sonnet-4-6",
                ReasoningEffort: null,
                ActiveProviderName: "Anthropic",
                ThinkingMode: "off"),
            CancellationToken.None);

        IReadOnlyList<SavedProviderConfiguration> providers =
            await sut.ListProvidersAsync(CancellationToken.None);
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        providers.Select(static provider => provider.Name)
            .Should()
            .Equal("Anthropic", "OpenAI");
        providers.Single(provider => provider.Name == "OpenAI").PreferredModelId.Should().Be("gpt-5.4");
        providers.Single(provider => provider.Name == "Anthropic").PreferredModelId.Should().Be("claude-sonnet-4-6");
        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.Anthropic, null),
            "claude-sonnet-4-6",
            ReasoningEffort: null,
            ActiveProviderName: "Anthropic",
            ThinkingMode: "off"));

        await sut.SetActiveProviderAsync("OpenAI", CancellationToken.None);

        loadedConfiguration = await sut.LoadAsync(CancellationToken.None);
        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5.4",
            ReasoningEffort: null,
            ActiveProviderName: "OpenAI",
            ThinkingMode: "off"));
    }

    [Fact]
    public async Task SaveAsync_Should_IgnoreMalformedSavedProviderEntries_When_SavingNewProvider()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "activeProviderName": "Broken",
              "providers": {
                "Broken": null
              }
            }
            """,
            CancellationToken.None);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "OpenAI");

        await sut.SaveAsync(configuration, CancellationToken.None);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);
        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task LoadAsync_Should_PreferEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "openrouter");
        using EnvironmentVariableScope model = new("STEMCODE_MODEL", " openai/gpt-4o ");
        using EnvironmentVariableScope thinking = new("STEMCODE_THINKING", "on");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "providerProfile": {
                "providerKind": "OpenAi"
              },
              "preferredModelId": "gpt-5.4"
            }
            """,
            CancellationToken.None);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.OpenRouter, null),
            "openai/gpt-4o",
            ReasoningEffort: null,
            ActiveProviderName: "OpenRouter",
            ThinkingMode: "on"));
    }

    [Fact]
    public async Task LoadAsync_Should_ReadReasoningEffort_From_Environment()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "openai");
        using EnvironmentVariableScope reasoning = new("STEMCODE_REASONING", " High ");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            PreferredModelId: null,
            ReasoningEffort: "high",
            ActiveProviderName: "OpenAI"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateCompatibleEnvironmentConfiguration_When_BaseUrlIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "openai-compatible");
        using EnvironmentVariableScope baseUrl = new("STEMCODE_BASE_URL", "https://provider.example.com/");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(
                ProviderKind.OpenAiCompatible,
                "https://provider.example.com/v1"),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "OpenAI-compatible provider (provider.example.com)"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateAnthropicClaudeAccountEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "anthropic-claude-account");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.AnthropicClaudeAccount, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Anthropic Claude Pro/Max"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateGitHubCopilotEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "github-copilot");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.GitHubCopilot, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "GitHub Copilot"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateKiloCodeEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "kilo-code");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.KiloCode, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Kilo Code"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateOllamaEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "ollama");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.Ollama, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Ollama"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateLmStudioEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "lm-studio");
        using EnvironmentVariableScope baseUrl = new("STEMCODE_BASE_URL", "http://127.0.0.1:4321");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.LmStudio, "http://127.0.0.1:4321/v1"),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "LM Studio"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateOllamaCloudEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "ollama-cloud");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.OllamaCloud, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Ollama Cloud"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateCerebrasEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "cerebras");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.Cerebras, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Cerebras"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateGroqEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "groq");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.Groq, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "Groq"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateDeepSeekEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "deepseek");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.DeepSeek, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "DeepSeek"));
    }

    [Fact]
    public async Task LoadAsync_Should_CreateOpenCodeZenEnvironmentConfiguration_When_ProviderIsSet()
    {
        using EnvironmentVariableScope provider = new("STEMCODE_PROVIDER", "opencode-zen");
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().Be(new AgentConfiguration(
            new AgentProviderProfile(ProviderKind.OpenCodeZen, null),
            PreferredModelId: null,
            ReasoningEffort: null,
            ActiveProviderName: "OpenCode Zen"));
    }

    [Fact]
    public async Task SaveAsync_Should_PreserveMemoryAndMcpProfileSections()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "memory": {
                "requireApprovalForWrites": true,
                "allowAutoFailureObservation": true,
                "allowAutoManualLessons": false,
                "redactSecrets": true,
                "maxEntries": 500,
                "maxPromptChars": 12000,
                "disabled": false
              },
              "mcpServers": {
                "context7": {
                  "command": "npx",
                  "args": ["-y", "@upstash/context7-mcp"]
                }
              }
            }
            """,
            CancellationToken.None);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5.4",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);

        string savedJson = await File.ReadAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            CancellationToken.None);
        savedJson.Should().Contain("\"memory\"");
        savedJson.Should().Contain("\"mcpServers\"");
        savedJson.Should().Contain("\"context7\"");
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);
        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task SaveAsync_Should_PreserveUnknownDesktopProfileSection()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "desktop": {
                "workspaces": [
                  {
                    "name": "FinalAgent",
                    "path": "C:\\src\\FinalAgent",
                    "lastOpened": "2026-04-27T10:00:00+05:00"
                  }
                ]
              }
            }
            """,
            CancellationToken.None);
        JsonAgentConfigurationStore sut = new(pathProvider);
        AgentConfiguration configuration = new(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5.4",
            ReasoningEffort: null,
            ThinkingMode: "on");

        await sut.SaveAsync(configuration, CancellationToken.None);

        string savedJson = await File.ReadAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            CancellationToken.None);
        savedJson.Should().Contain("\"desktop\"");
        savedJson.Should().Contain("\"workspaces\"");
        savedJson.Should().Contain("\"FinalAgent\"");
        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);
        loadedConfiguration.Should().Be(configuration);
    }

    [Fact]
    public async Task LoadAsync_Should_NormalizeCompatibleProviderBaseUrl()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "providerProfile": {
                "providerKind": 2,
                "baseUrl": "https://provider.example.com/"
              },
              "preferredModelId": "gpt-4.1"
            }
            """,
            CancellationToken.None);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().NotBeNull();
        loadedConfiguration!.ProviderProfile.Should().Be(
            new AgentProviderProfile(
                ProviderKind.OpenAiCompatible,
                "https://provider.example.com/v1"));
    }

    [Fact]
    public async Task LoadAsync_Should_ReturnNull_When_CompatibleProviderBaseUrlIsInvalid()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "providerProfile": {
                "providerKind": 2,
                "baseUrl": "not-a-url"
              }
            }
            """,
            CancellationToken.None);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_Should_ClearLegacyThinkingValues()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            """
            {
              "providerProfile": {
                "providerKind": 1
              },
              "preferredModelId": "gpt-5.4",
              "reasoningEffort": "high"
            }
            """,
            CancellationToken.None);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().NotBeNull();
        loadedConfiguration!.ReasoningEffort.Should().Be("high");
        loadedConfiguration.ThinkingMode.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_Should_ReturnNull_When_ConfigurationJsonIsMalformed()
    {
        StubUserDataPathProvider pathProvider = new(_tempRoot);
        JsonAgentConfigurationStore sut = new(pathProvider);

        await File.WriteAllTextAsync(
            pathProvider.GetConfigurationFilePath(),
            "{",
            CancellationToken.None);

        AgentConfiguration? loadedConfiguration = await sut.LoadAsync(CancellationToken.None);

        loadedConfiguration.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }

        foreach (EnvironmentVariableScope environmentVariable in _environmentVariables)
        {
            environmentVariable.Dispose();
        }
    }

    private sealed class StubUserDataPathProvider : IUserDataPathProvider
    {
        private readonly string _root;

        public StubUserDataPathProvider(string root)
        {
            _root = root;
        }

        public string GetConfigurationFilePath()
        {
            return Path.Combine(_root, "agent-profile.json");
        }

        public string GetMcpConfigurationFilePath()
        {
            return Path.Combine(_root, "mcp.toml");
        }

        public string GetLogsDirectoryPath()
        {
            return Path.Combine(_root, "logs");
        }

        public string GetSessionsDirectoryPath()
        {
            return Path.Combine(_root, "sessions");
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _previousValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _previousValue);
        }
    }
}
