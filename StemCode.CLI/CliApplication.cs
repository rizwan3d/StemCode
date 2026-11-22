using StemCode.Application.Backend;
using StemCode.Infrastructure.Telemetry;

namespace StemCode.CLI;

internal static class CliApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (SandboxInvocationParser.TryHandleSpecialInvocation(args, out int specialExitCode))
        {
            return specialExitCode;
        }

        if (VoiceConsoleRunner.IsInvocation(args))
        {
            return await VoiceConsoleRunner.RunAsync(args);
        }

        CliInvocation invocation;
        try
        {
            invocation = CliInvocation.Parse(
                args,
                Console.IsInputRedirected,
                Console.In.ReadToEnd);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine();
            WriteUsage(Console.Error);
            return ExitCodeMapper.UsageError;
        }

        if (invocation.ShowHelp)
        {
            WriteUsage(Console.Out);
            return ExitCodeMapper.Success;
        }

        if (invocation.ShowVersion)
        {
            Console.Out.WriteLine(GetVersionText());
            return ExitCodeMapper.Success;
        }

        if (invocation.WorktreeName is not null)
        {
            return await CliWorktreeSession.RunAsync(
                invocation.WorktreeName,
                () => RunInvocationAsync(invocation));
        }

        return await RunInvocationAsync(invocation);
    }

    private static async Task<int> RunInvocationAsync(CliInvocation invocation)
    {
        BackendRuntimeArguments runtimeArguments = invocation.RuntimeArguments.WithDefaults(
            BackendRuntimeOptions.CliSurface);

        return invocation.Mode switch
        {
            CliMode.SingleTurn => await SingleTurnRunner.RunAsync(
                runtimeArguments,
                invocation.ProviderAuthKey,
                invocation.Prompt ?? string.Empty,
                invocation.JsonOutput,
                invocation.AutoApproveAllTools,
                invocation.StreamJsonOutput),
            CliMode.Acp => await AcpHost.RunAsync(
                runtimeArguments.RawArgs,
                invocation.ProviderAuthKey,
                invocation.NoOldReader,
                invocation.AutoApproveAllTools),
            _ => await InteractiveTerminalHost.RunAsync(
                runtimeArguments,
                invocation.ProviderAuthKey,
                invocation.NoOldReader,
                invocation.AutoApproveAllTools)
        };
    }

    internal static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine(
            $"""
            {GetVersionText()}

            Usage:
              stemcode [options]                    Start the interactive terminal UI
              stemcode [options] "<prompt>"         Run one prompt and print the response
              stemcode [options] --prompt "<text>"  Run one prompt and print the response
              echo "<prompt>" | stemcode [options]  Run one prompt from standard input
              stemcode --acp [options]              Run an Agent Client Protocol server

            Options:
              --acp                Speak ACP over stdin/stdout for compatible editors
              --interactive        Start the terminal UI explicitly
              --stdin              Read the one-shot prompt from standard input
              --json               Write one-shot result as a JSON object
              --output-format <text|json|stream-json>
                                   Output for one-shot runs; stream-json writes one JSON event per line
                                   (init, assistant_delta, reasoning, tool_use, tool_result, plan, retry)
                                   and ends with the --json result object
              -m, --model <id>     Use this model for this run (not saved as the default)
              --max-turns <n>      Stop after n sequential tool rounds in one turn
              --allowedTools <list>
                                   Allow these tools without prompting, e.g. "file_write shell_command(git *)"
              --disallowedTools <list>
                                   Deny these tools; entries without a (pattern) are hidden from the model
              --append-system-prompt <text>
                                   Append text to the main agent's system prompt for this run
              --append-system-prompt-file <path>
                                   Append a file's contents to the main agent's system prompt
              -y, --yes            Approve promptable tool requests for this run
              -p, --prompt <text>  One-shot prompt text
              --sandbox-mode <mode>
                                   Override sandbox mode: read-only, workspace-write, or danger-full-access
              --provider-auth-key <key>
                                   Use this key for provider API-key onboarding
              --section <id>       Resume an existing section
              --session <id>       Alias for --section
              --no-update-check    Skip checking for application updates on startup
              --no-old-reader      Resume a section without replaying old messages to the screen
              --profile <name>     Use an agent profile
              --thinking <on|off>  Override thinking mode
              -w, --worktree[=<name>]
                                   Run this session in a new git worktree and branch so parallel
                                   sessions don't edit the same files; removed on exit if unchanged
              -v, --version        Show version
              -h, --help           Show help
              --doctor             Run system diagnostics and print doctor report

            Note:
              Run stemcode once to complete provider setup before using one-shot prompts.
            """);
    }

    internal static string GetVersionText()
    {
        return $"StemCode CLI {ProductTelemetryHelpers.GetStemCodeVersion()}";
    }
}
