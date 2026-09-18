using FluentAssertions;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Application.Permissions;
using StemCode.Application.Tools;
using StemCode.Application.Tools.Models;
using StemCode.Application.Tools.Serialization;
using StemCode.Application.Tools.Services;
using StemCode.Domain.Models;
using System.Globalization;
using System.Text.Json;

namespace StemCode.Tests.Application.Tools.Services;

public sealed class RegistryBackedToolInvokerTests
{
    private static readonly PermissionSettings DefaultPermissionSettings = new()
    {
        DefaultMode = PermissionMode.Ask,
        Rules = []
    };
    private static readonly ReplSessionContext Session = new(
        new AgentProviderProfile(ProviderKind.OpenAi, null),
        "gpt-5-mini",
        ["gpt-5-mini"]);
    private static readonly ReplSessionContext DeepSeekSession = new(
        new AgentProviderProfile(ProviderKind.DeepSeek, null),
        "deepseek-v4-pro",
        ["deepseek-v4-pro"]);

    [Fact]
    public async Task InvokeAsync_Should_ReturnNotFoundResult_When_ToolIsUnknown()
    {
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "missing_tool", "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("missing_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.NotFound);
        result.Result.Message.Should().Contain("not registered");
        result.Result.JsonResult.Should().Contain("tool_not_found");
        result.ToolNameRecognized.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnInvalidArguments_When_ToolArgumentsAreNotJsonObject()
    {
        RegistryBackedToolInvoker sut = new(new ToolRegistry([
            new EchoTool()
        ], new ToolPermissionParser()), new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings), new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "echo_tool", "[]"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("echo_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.InvalidArguments);
        result.Result.Message.Should().Contain("JSON-object arguments");
    }

    [Fact]
    public async Task InvokeAsync_Should_RepairStringifiedArrayArguments_ForDeepSeekSessions()
    {
        ArrayCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", tool.Name, """{ "items": "[\"alpha\",\"beta\"]" }"""),
            DeepSeekSession,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.Items.Should().Equal("alpha", "beta");
    }

    [Fact]
    public async Task InvokeAsync_Should_RepairBareStringArrayArguments_ForDeepSeekSessions()
    {
        ArrayCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", tool.Name, """{ "items": "alpha" }"""),
            DeepSeekSession,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.Items.Should().Equal("alpha");
    }

    [Fact]
    public async Task InvokeAsync_Should_RepairSingleObjectArrayArguments_ForDeepSeekSessions()
    {
        ObjectArrayCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", tool.Name, """{ "tasks": { "task": "review", "context": null } }"""),
            DeepSeekSession,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.TaskCount.Should().Be(1);
        tool.FirstTask.Should().Be("review");
        tool.FirstTaskHadContext.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Should_UnwrapMarkdownPaths_And_RemoveOptionalNulls_ForDeepSeekSessions()
    {
        PathCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", tool.Name, """{ "path": "docs/[notes.md](http://notes.md)", "overwrite": null }"""),
            DeepSeekSession,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.Path.Should().Be("docs/notes.md");
        tool.HadOverwrite.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Should_IgnoreDuplicateArgumentKeys_DuringDeepSeekRepair()
    {
        PathCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall(
                "call_1",
                tool.Name,
                """{ "path": "docs/notes.md", "overwrite": false, "overwrite": true }"""),
            DeepSeekSession,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.Path.Should().Be("docs/notes.md");
        tool.HadOverwrite.Should().BeTrue();
        tool.Overwrite.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_NotRepairArguments_ForNonDeepSeekSessions()
    {
        ArrayCaptureTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", tool.Name, """{ "items": "[\"alpha\",\"beta\"]" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(tool.Name),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.InvalidArguments);
        result.Result.Message.Should().Contain("requires 'items' to be an array");
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnExecutionErrorResult_When_ToolThrowsUnexpectedly()
    {
        RegistryBackedToolInvoker sut = new(new ToolRegistry([
            new ThrowingTool()
        ], new ToolPermissionParser()), new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings), new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "exploding_tool", "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("exploding_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.Result.Message.Should().Contain("boom");
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnExecutionError_When_ToolTimesOut()
    {
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new SlowTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce),
            TimeSpan.FromMilliseconds(50));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "slow_tool", "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("slow_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.Result.Message.Should().Contain("timed out");
    }

    [Fact]
    public async Task InvokeAsync_Should_UseConfiguredDefaultToolTimeout()
    {
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new SlowTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce),
            toolExecutionSettings: new ToolExecutionSettings
            {
                DefaultTimeoutSeconds = 1
            });

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "slow_tool", "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("slow_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.Result.Message.Should().Contain("timed out after 1 seconds");
    }

    [Fact]
    public async Task InvokeAsync_Should_ExecuteTool_When_ApprovalPromptAllowsOnce()
    {
        ApprovalTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.AllowOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "approval_tool", """{ "path": "src/app.cs" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("approval_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        tool.WasExecuted.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_CacheSuccessfulFileReadsWithEquivalentArguments()
    {
        string workspacePath = CreateTempWorkspace(("README.md", "hello"));
        try
        {
            CountingReadTool readTool = new();
            RegistryBackedToolInvoker sut = CreateAutomaticInvoker(readTool);
            ReplSessionContext session = CreateWorkspaceSession(workspacePath);

            ToolInvocationResult first = await sut.InvokeAsync(
                new ConversationToolCall("call_1", AgentToolNames.FileRead, """{ "path": "README.md", "limit": 20 }"""),
                session,
                ConversationExecutionPhase.Execution,
                CreateAllowedToolNames(AgentToolNames.FileRead),
                CancellationToken.None);
            ToolInvocationResult second = await sut.InvokeAsync(
                new ConversationToolCall("call_2", AgentToolNames.FileRead, """{ "limit": 20, "path": "README.md" }"""),
                session,
                ConversationExecutionPhase.Execution,
                CreateAllowedToolNames(AgentToolNames.FileRead),
                CancellationToken.None);

            first.Result.Status.Should().Be(ToolResultStatus.Success);
            second.Result.Status.Should().Be(ToolResultStatus.Success);
            second.ToolCallId.Should().Be("call_2");
            second.Result.JsonResult.Should().Be(first.Result.JsonResult);
            readTool.ExecuteCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_Should_ClearReadCacheAfterMutatingTool()
    {
        string workspacePath = CreateTempWorkspace(("README.md", "hello"));
        try
        {
            CountingReadTool readTool = new();
            MutatingWriteTool writeTool = new();
            RegistryBackedToolInvoker sut = CreateAutomaticInvoker(readTool, writeTool);
            ReplSessionContext session = CreateWorkspaceSession(workspacePath);

            await sut.InvokeAsync(
                new ConversationToolCall("call_1", AgentToolNames.FileRead, """{ "path": "README.md" }"""),
                session,
                ConversationExecutionPhase.Execution,
                CreateAllowedToolNames(AgentToolNames.FileRead, AgentToolNames.FileWrite),
                CancellationToken.None);
            await sut.InvokeAsync(
                new ConversationToolCall("call_2", AgentToolNames.FileWrite, """{ "path": "README.md" }"""),
                session,
                ConversationExecutionPhase.Execution,
                CreateAllowedToolNames(AgentToolNames.FileRead, AgentToolNames.FileWrite),
                CancellationToken.None);
            await sut.InvokeAsync(
                new ConversationToolCall("call_3", AgentToolNames.FileRead, """{ "path": "README.md" }"""),
                session,
                ConversationExecutionPhase.Execution,
                CreateAllowedToolNames(AgentToolNames.FileRead, AgentToolNames.FileWrite),
                CancellationToken.None);

            readTool.ExecuteCount.Should().Be(2);
            writeTool.ExecuteCount.Should().Be(1);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task InvokeAsync_Should_CacheSuccessfulSafeShellChecks()
    {
        CountingShellStatusTool shellTool = new();
        RegistryBackedToolInvoker sut = CreateAutomaticInvoker(shellTool);

        ToolInvocationResult first = await sut.InvokeAsync(
            new ConversationToolCall("call_1", AgentToolNames.ShellCommand, """{ "command": "git status --short" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(AgentToolNames.ShellCommand),
            CancellationToken.None);
        ToolInvocationResult second = await sut.InvokeAsync(
            new ConversationToolCall("call_2", AgentToolNames.ShellCommand, """{ "command": "git status --short" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(AgentToolNames.ShellCommand),
            CancellationToken.None);

        first.Result.Status.Should().Be(ToolResultStatus.Success);
        second.Result.Status.Should().Be(ToolResultStatus.Success);
        second.ToolCallId.Should().Be("call_2");
        second.Result.JsonResult.Should().Be(first.Result.JsonResult);
        shellTool.ExecuteCount.Should().Be(1);
    }

    [Fact]
    public async Task InvokeAsync_Should_RunLifecycleHooksAroundToolCall()
    {
        RecordingLifecycleHookService hookService = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new EchoTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce),
            lifecycleHookService: hookService);

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "echo_tool", "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("echo_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        hookService.Events.Should().Equal(
            LifecycleHookEvents.BeforeToolCall,
            LifecycleHookEvents.AfterToolCall);
    }

    [Fact]
    public async Task InvokeAsync_Should_BlockTool_When_BeforeHookBlocks()
    {
        HookVisibleTool tool = new(AgentToolNames.FileWrite);
        RecordingLifecycleHookService hookService = new(LifecycleHookEvents.BeforeFileWrite);
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce),
            lifecycleHookService: hookService);

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", AgentToolNames.FileWrite, """{ "path": "src/app.cs" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(AgentToolNames.FileWrite),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.ExecutionError);
        result.Result.JsonResult.Should().Contain("lifecycle_hook_blocked");
        tool.WasExecuted.Should().BeFalse();
        hookService.Events.Should().Equal(
            LifecycleHookEvents.BeforeToolCall,
            LifecycleHookEvents.BeforeFileWrite);
    }

    [Fact]
    public async Task InvokeAsync_Should_RunShellFailureHook_When_ShellExitsNonZero()
    {
        RecordingLifecycleHookService hookService = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new ShellResultTool(exitCode: 2)], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce),
            lifecycleHookService: hookService);

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", AgentToolNames.ShellCommand, """{ "command": "dotnet test" }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames(AgentToolNames.ShellCommand),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.Success);
        hookService.Events.Should().Contain(LifecycleHookEvents.AfterShellFailure);
        hookService.Contexts.Should().Contain(context =>
            context.EventName == LifecycleHookEvents.AfterShellFailure &&
            context.ShellCommand == "dotnet test" &&
            context.ShellExitCode == 2);
    }

    [Fact]
    public async Task InvokeAsync_Should_RememberAllowOverride_When_ApprovalPromptAllowsForAgent()
    {
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5-mini",
            ["gpt-5-mini"]);
        ApprovalTool tool = new();
        FixedPermissionApprovalPrompt prompt = new(PermissionApprovalChoice.AllowForAgent);
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            prompt);

        ToolInvocationResult first = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "approval_tool", """{ "path": "src/app.cs" }"""),
            session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("approval_tool"),
            CancellationToken.None);
        ToolInvocationResult second = await sut.InvokeAsync(
            new ConversationToolCall("call_2", "approval_tool", """{ "path": "src/app.cs" }"""),
            session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("approval_tool"),
            CancellationToken.None);

        first.Result.Status.Should().Be(ToolResultStatus.Success);
        second.Result.Status.Should().Be(ToolResultStatus.Success);
        prompt.PromptCount.Should().Be(1);
        session.PermissionOverrides.Should().ContainSingle();
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnPermissionDenied_When_ApprovalPromptDeniesForAgent()
    {
        ReplSessionContext session = new(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5-mini",
            ["gpt-5-mini"]);
        ApprovalTool tool = new();
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([tool], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyForAgent));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "approval_tool", """{ "path": "src/app.cs" }"""),
            session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("approval_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.Result.Message.Should().ContainEquivalentOf("denied");
        tool.WasExecuted.Should().BeFalse();
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnPermissionDenied_When_ShellCommandMatchesDenyRule()
    {
        PermissionSettings settings = new()
        {
            DefaultMode = PermissionMode.Ask,
            Rules =
            [
                new PermissionRule
                {
                    Tools = ["bash"],
                    Mode = PermissionMode.Deny,
                    Patterns = ["rm -rf*"]
                }
            ]
        };

        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new ShellRestrictedTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), settings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "shell_restricted_tool", """{ "command": "rm -rf ." }"""),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("shell_restricted_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.Result.Message.Should().ContainEquivalentOf("denied");
    }

    [Fact]
    public async Task InvokeAsync_Should_ReturnPermissionDenied_When_ToolIsNotAvailableInPhase()
    {
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new EchoTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", "echo_tool", "{}"),
            Session,
            ConversationExecutionPhase.Planning,
            CreateAllowedToolNames("file_read"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.Result.JsonResult.Should().Contain("tool_not_available_in_phase");
        result.Result.Message.Should().Contain("planning phase");
        result.ToolNameRecognized.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_Should_MarkToolNameUnrecognized_When_NameIsGarbageAndNotRegistered()
    {
        RegistryBackedToolInvoker sut = new(
            new ToolRegistry([new EchoTool()], new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));

        string garbageName = "read_file_placeholder\n\npath: README.md\n\ntext_search";
        ToolInvocationResult result = await sut.InvokeAsync(
            new ConversationToolCall("call_1", garbageName, "{}"),
            Session,
            ConversationExecutionPhase.Execution,
            CreateAllowedToolNames("echo_tool"),
            CancellationToken.None);

        result.Result.Status.Should().Be(ToolResultStatus.PermissionDenied);
        result.ToolNameRecognized.Should().BeFalse();
    }

    private static IReadOnlySet<string> CreateAllowedToolNames(params string[] toolNames)
    {
        return new HashSet<string>(toolNames, StringComparer.Ordinal);
    }

    private static RegistryBackedToolInvoker CreateAutomaticInvoker(params ITool[] tools)
    {
        return new RegistryBackedToolInvoker(
            new ToolRegistry(tools, new ToolPermissionParser()),
            new ToolPermissionEvaluator(new StubWorkspaceRootProvider(), DefaultPermissionSettings),
            new FixedPermissionApprovalPrompt(PermissionApprovalChoice.DenyOnce));
    }

    private static ReplSessionContext CreateWorkspaceSession(string workspacePath)
    {
        return new ReplSessionContext(
            new AgentProviderProfile(ProviderKind.OpenAi, null),
            "gpt-5-mini",
            ["gpt-5-mini"],
            workspacePath: workspacePath);
    }

    private static string CreateTempWorkspace(params (string RelativePath, string Content)[] files)
    {
        string workspacePath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"stemcode-registry-invoker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        foreach ((string relativePath, string content) in files)
        {
            string fullPath = System.IO.Path.Combine(workspacePath, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        return workspacePath;
    }

    private sealed class CountingReadTool : ITool
    {
        public int ExecuteCount { get; private set; }

        public string Description => "Counting read tool";

        public string Name => AgentToolNames.FileRead;

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": { "path": { "type": "string" }, "limit": { "type": "integer" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult(ToolResultFactory.Success(
                $"Read count {ExecuteCount}.",
                new ToolErrorPayload("ok", ExecuteCount.ToString(CultureInfo.InvariantCulture)),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class MutatingWriteTool : ITool
    {
        public int ExecuteCount { get; private set; }

        public string Description => "Mutating write tool";

        public string Name => AgentToolNames.FileWrite;

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": { "path": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult(ToolResultFactory.Success(
                "Wrote file.",
                new ToolErrorPayload("ok", "wrote"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class CountingShellStatusTool : ITool
    {
        public int ExecuteCount { get; private set; }

        public string Description => "Counting shell status tool";

        public string Name => AgentToolNames.ShellCommand;

        public string PermissionRequirements => """
            {
              "approvalMode": "Automatic",
              "toolTags": ["bash"],
              "shell": {
                "commandArgumentName": "command"
              }
            }
            """;

        public string Schema => """{ "type": "object", "properties": { "command": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult(ToolResultFactory.Success(
                "Git status complete.",
                new ShellCommandExecutionResult(
                    "git status --short",
                    ".",
                    0,
                    $"status call {ExecuteCount}",
                    string.Empty),
                ToolJsonContext.Default.ShellCommandExecutionResult));
        }
    }

    private sealed class EchoTool : ITool
    {
        public string Description => "Echo tool";

        public string Name => "echo_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": {}, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ToolResultFactory.Success(
                "Echoed.",
                new ToolErrorPayload("echo", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class SlowTool : ITool
    {
        public string Description => "Slow tool";

        public string Name => "slow_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": {}, "additionalProperties": false }""";

        public async Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return ToolResultFactory.Success(
                "Completed.",
                new ToolErrorPayload("slow", "done"),
                ToolJsonContext.Default.ToolErrorPayload);
        }
    }

    private sealed class ArrayCaptureTool : ITool
    {
        public IReadOnlyList<string> Items { get; private set; } = [];

        public string Description => "Array capture tool";

        public string Name => "array_capture_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """
            {
              "type": "object",
              "properties": {
                "items": {
                  "type": "array",
                  "items": {
                    "type": "string"
                  }
                }
              },
              "required": ["items"],
              "additionalProperties": false
            }
            """;

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.Arguments.TryGetProperty("items", out JsonElement itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ToolResultFactory.InvalidArguments(
                    "invalid_items",
                    "Tool 'array_capture_tool' requires 'items' to be an array.",
                    new ToolRenderPayload(
                        "Invalid array capture arguments",
                        "Provide 'items' as an array.")));
            }

            Items = itemsElement
                .EnumerateArray()
                .Select(static item => item.GetString() ?? string.Empty)
                .ToArray();

            return Task.FromResult(ToolResultFactory.Success(
                "Captured items.",
                new ToolErrorPayload("ok", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class ObjectArrayCaptureTool : ITool
    {
        public string? FirstTask { get; private set; }

        public bool FirstTaskHadContext { get; private set; }

        public int TaskCount { get; private set; }

        public string Description => "Object array capture tool";

        public string Name => "object_array_capture_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """
            {
              "type": "object",
              "properties": {
                "tasks": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "task": {
                        "type": "string"
                      },
                      "context": {
                        "type": "string"
                      }
                    },
                    "required": ["task"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["tasks"],
              "additionalProperties": false
            }
            """;

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (!context.Arguments.TryGetProperty("tasks", out JsonElement tasksElement) ||
                tasksElement.ValueKind != JsonValueKind.Array)
            {
                return Task.FromResult(ToolResultFactory.InvalidArguments(
                    "invalid_tasks",
                    "Tool 'object_array_capture_tool' requires 'tasks' to be an array.",
                    new ToolRenderPayload(
                        "Invalid object array arguments",
                        "Provide 'tasks' as an array.")));
            }

            TaskCount = tasksElement.GetArrayLength();
            JsonElement firstTask = tasksElement.EnumerateArray().First();
            FirstTask = firstTask.GetProperty("task").GetString();
            FirstTaskHadContext = firstTask.TryGetProperty("context", out _);

            return Task.FromResult(ToolResultFactory.Success(
                "Captured tasks.",
                new ToolErrorPayload("ok", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class PathCaptureTool : ITool
    {
        public bool HadOverwrite { get; private set; }

        public bool? Overwrite { get; private set; }

        public string? Path { get; private set; }

        public string Description => "Path capture tool";

        public string Name => "path_capture_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """
            {
              "type": "object",
              "properties": {
                "path": {
                  "type": "string"
                },
                "overwrite": {
                  "type": "boolean"
                }
              },
              "required": ["path"],
              "additionalProperties": false
            }
            """;

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            Path = context.Arguments.GetProperty("path").GetString();
            HadOverwrite = context.Arguments.TryGetProperty("overwrite", out _);
            Overwrite = ToolArguments.TryGetBoolean(context.Arguments, "overwrite", out bool overwrite)
                ? overwrite
                : null;

            return Task.FromResult(ToolResultFactory.Success(
                "Captured path.",
                new ToolErrorPayload("ok", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class ThrowingTool : ITool
    {
        public string Description => "Throwing tool";

        public string Name => "exploding_tool";

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": {}, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ApprovalTool : ITool
    {
        public bool WasExecuted { get; private set; }

        public string Description => "Approval tool";

        public string Name => "approval_tool";

        public string PermissionRequirements => """
            {
              "approvalMode": "RequireApproval",
              "filePaths": [
                {
                  "argumentName": "path",
                  "kind": "Read",
                  "allowedRoots": ["src"]
                }
              ]
            }
            """;

        public string Schema => """{ "type": "object", "properties": { "path": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.FromResult(ToolResultFactory.Success(
                "Executed.",
                new ToolErrorPayload("ok", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class ShellRestrictedTool : ITool
    {
        public string Description => "Shell restricted tool";

        public string Name => "shell_restricted_tool";

        public string PermissionRequirements => """
            {
              "approvalMode": "Automatic",
              "toolTags": ["bash"],
              "shell": {
                "commandArgumentName": "command"
              }
            }
            """;

        public string Schema => """{ "type": "object", "properties": { "command": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class HookVisibleTool : ITool
    {
        private readonly string _name;

        public HookVisibleTool(string name)
        {
            _name = name;
        }

        public bool WasExecuted { get; private set; }

        public string Description => "Hook visible tool";

        public string Name => _name;

        public string PermissionRequirements => """{ "approvalMode": "Automatic" }""";

        public string Schema => """{ "type": "object", "properties": { "path": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            WasExecuted = true;
            return Task.FromResult(ToolResultFactory.Success(
                "Executed.",
                new ToolErrorPayload("ok", "ok"),
                ToolJsonContext.Default.ToolErrorPayload));
        }
    }

    private sealed class ShellResultTool : ITool
    {
        private readonly int _exitCode;

        public ShellResultTool(int exitCode)
        {
            _exitCode = exitCode;
        }

        public string Description => "Shell result tool";

        public string Name => AgentToolNames.ShellCommand;

        public string PermissionRequirements => """
            {
              "approvalMode": "Automatic",
              "toolTags": ["bash"],
              "shell": {
                "commandArgumentName": "command"
              }
            }
            """;

        public string Schema => """{ "type": "object", "properties": { "command": { "type": "string" } }, "additionalProperties": false }""";

        public Task<ToolResult> ExecuteAsync(
            ToolExecutionContext context,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ToolResultFactory.Success(
                "Shell completed.",
                new ShellCommandExecutionResult("dotnet test", ".", _exitCode, string.Empty, "failed"),
                ToolJsonContext.Default.ShellCommandExecutionResult));
        }
    }

    private sealed class RecordingLifecycleHookService : ILifecycleHookService
    {
        private readonly string? _blockedEvent;

        public RecordingLifecycleHookService(string? blockedEvent = null)
        {
            _blockedEvent = blockedEvent;
        }

        public List<LifecycleHookContext> Contexts { get; } = [];

        public IReadOnlyList<string> Events => Contexts.Select(static context => context.EventName).ToArray();

        public Task<LifecycleHookRunResult> RunAsync(
            LifecycleHookContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);

            if (string.Equals(context.EventName, _blockedEvent, StringComparison.Ordinal))
            {
                return Task.FromResult(LifecycleHookRunResult.Blocked(
                    "test-hook",
                    $"Blocked {context.EventName}."));
            }

            return Task.FromResult(LifecycleHookRunResult.Allowed());
        }
    }

    private sealed class StubWorkspaceRootProvider : IWorkspaceRootProvider
    {
        public string GetWorkspaceRoot()
        {
            return Path.GetTempPath();
        }
    }

    private sealed class FixedPermissionApprovalPrompt : IPermissionApprovalPrompt
    {
        private readonly PermissionApprovalChoice _choice;

        public FixedPermissionApprovalPrompt(PermissionApprovalChoice choice)
        {
            _choice = choice;
        }

        public int PromptCount { get; private set; }

        public Task<PermissionApprovalChoice> PromptAsync(
            PermissionApprovalRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PromptCount++;
            return Task.FromResult(_choice);
        }
    }
}
