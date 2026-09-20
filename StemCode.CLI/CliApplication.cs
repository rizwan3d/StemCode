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

        BackendRuntimeArguments runtimeArguments = invocation.RuntimeArguments.WithDefaults(
            BackendRuntimeOptions.CliSurface);

        return invocation.Mode switch
        {
            CliMode.SingleTurn => await SingleTurnRunner.RunAsync(
                runtimeArguments,
                invocation.ProviderAuthKey,
                invocation.Prompt ?? string.Empty,
                invocation.JsonOutput,
                invocation.AutoApproveAllTools),
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
              --context-size <n>   Cap context window for this run
                                   Presets: 8k, 32k, 64k, 125k, 256k; custom token counts supported
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
