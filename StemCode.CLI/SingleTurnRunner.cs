using StemCode.Application.Backend;
using StemCode.Application.Commands;
using StemCode.Application.Exceptions;
using StemCode.Application.Models;
using StemCode.Application.UI;

namespace StemCode.CLI;

internal static class SingleTurnRunner
{
    public static async Task<int> RunAsync(
        BackendRuntimeArguments runtimeArguments,
        string? providerAuthKey,
        string prompt,
        bool jsonOutput,
        bool autoApproveAllTools,
        bool streamJsonOutput = false)
    {
        // stream-json ends with the same result/error object --json prints, so the
        // final-output paths below treat it as JSON output.
        jsonOutput = jsonOutput || streamJsonOutput;

        if (string.IsNullOrWhiteSpace(prompt))
        {
            WriteError(jsonOutput, "missing_prompt", "No prompt was provided.");
            return ExitCodeMapper.UsageError;
        }

        ConsoleBridge consoleBridge = new(providerAuthKey);
        StreamJsonBridge? streamBridge = streamJsonOutput
            ? new StreamJsonBridge(consoleBridge, Console.Out)
            : null;
        IUiBridge uiBridge = streamBridge is null ? consoleBridge : streamBridge;
        await using IStemCodeBackend backend = new StemCodeBackend(
            runtimeArguments,
            sessionMcpServers: [],
            autoApproveAllTools);
        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler cancelKeyPressHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelKeyPressHandler;

        try
        {
            BackendSessionInfo sessionInfo = await backend.InitializeAsync(uiBridge, cancellation.Token);
            streamBridge?.WriteInit(sessionInfo);

            string normalizedPrompt = prompt.Trim();
            if (normalizedPrompt.StartsWith("/", StringComparison.Ordinal))
            {
                return await RunSlashCommandAsync(
                    backend,
                    sessionInfo,
                    normalizedPrompt,
                    jsonOutput,
                    uiBridge,
                    cancellation.Token);
            }

            ConversationTurnResult result = await backend.RunTurnAsync(
                normalizedPrompt,
                uiBridge,
                cancellation.Token);

            Console.WriteLine(jsonOutput
                ? CliJsonOutputWriter.FormatTurn(result, sessionInfo)
                : result.ResponseText);
            return ExitCodeMapper.Success;
        }
        catch (PromptCancelledException exception)
        {
            WriteError(jsonOutput, "prompt_cancelled", exception.Message);
            return ExitCodeMapper.Error;
        }
        catch (OperationCanceledException)
        {
            WriteError(jsonOutput, "cancelled", "Cancelled.");
            return ExitCodeMapper.Cancelled;
        }
        catch (Exception exception)
        {
            WriteError(jsonOutput, "error", exception.Message);
            return ExitCodeMapper.Error;
        }
        finally
        {
            Console.CancelKeyPress -= cancelKeyPressHandler;
        }
    }

    private static async Task<int> RunSlashCommandAsync(
        IStemCodeBackend backend,
        BackendSessionInfo sessionInfo,
        string normalizedPrompt,
        bool jsonOutput,
        IUiBridge uiBridge,
        CancellationToken cancellationToken)
    {
        if (CustomSlashCommandService.TryExpand(
                Directory.GetCurrentDirectory(),
                normalizedPrompt,
                out CustomSlashCommandResolution? customCommand,
                out string? customCommandError))
        {
            if (customCommand is null)
            {
                WriteError(
                    jsonOutput,
                    "custom_command_error",
                    customCommandError ?? "Custom command could not be expanded.");
                return ExitCodeMapper.Error;
            }

            ConversationTurnResult customResult = await backend.RunTurnAsync(
                customCommand.ExpandedPrompt,
                uiBridge,
                cancellationToken);

            Console.WriteLine(jsonOutput
                ? CliJsonOutputWriter.FormatTurn(customResult, sessionInfo)
                : customResult.ResponseText);
            return ExitCodeMapper.Success;
        }

        BackendCommandResult commandResult = await backend.RunCommandAsync(
            normalizedPrompt,
            cancellationToken);

        if (jsonOutput)
        {
            Console.WriteLine(CliJsonOutputWriter.FormatCommand(commandResult));
        }
        else
        {
            WriteCommandResult(commandResult.CommandResult);
        }

        return commandResult.CommandResult.FeedbackKind == ReplFeedbackKind.Error
            ? ExitCodeMapper.Error
            : ExitCodeMapper.Success;
    }

    private static void WriteError(
        bool jsonOutput,
        string errorCode,
        string message)
    {
        if (jsonOutput)
        {
            Console.WriteLine(CliJsonOutputWriter.FormatError(errorCode, message));
            return;
        }

        Console.Error.WriteLine(errorCode == "error"
            ? $"StemCode error: {message}"
            : message);
    }

    private static void WriteCommandResult(ReplCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Message))
        {
            return;
        }

        TextWriter writer = result.FeedbackKind == ReplFeedbackKind.Info
            ? Console.Out
            : Console.Error;

        string prefix = result.FeedbackKind switch
        {
            ReplFeedbackKind.Error => "Error: ",
            ReplFeedbackKind.Warning => "Warning: ",
            _ => string.Empty
        };

        writer.WriteLine(prefix + result.Message.Trim());
    }
}
