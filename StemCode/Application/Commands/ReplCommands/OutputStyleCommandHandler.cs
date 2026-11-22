using StemCode.Application.Models;
using StemCode.Application.Services;
using System.Text;

namespace StemCode.Application.Commands;

internal sealed class OutputStyleCommandHandler : IReplCommandHandler
{
    public const string UsageText = "/output-style [list|<style>]";

    private readonly IOutputStyleService _outputStyleService;

    public OutputStyleCommandHandler(IOutputStyleService outputStyleService)
    {
        _outputStyleService = outputStyleService;
    }

    public string CommandName => "output-style";

    public string Description => "Show or switch the output style that shapes how the agent writes its answers.";

    public string Usage => UsageText;

    public async Task<ReplCommandResult> ExecuteAsync(
        ReplCommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string argument = context.ArgumentText.Trim();
        if (argument.Length == 0 || string.Equals(argument, "list", StringComparison.OrdinalIgnoreCase))
        {
            return ReplCommandResult.Continue(BuildListing(), ReplFeedbackKind.Info);
        }

        try
        {
            OutputStyle style = await _outputStyleService.SetActiveAsync(argument, cancellationToken);
            return ReplCommandResult.Continue(
                $"Output style set to '{style.Name}': {style.Description} It applies from your next message.",
                ReplFeedbackKind.Info);
        }
        catch (ArgumentException exception)
        {
            return ReplCommandResult.Continue(exception.Message, ReplFeedbackKind.Error);
        }
    }

    private string BuildListing()
    {
        OutputStyle active = _outputStyleService.Active;
        StringBuilder builder = new();
        builder.AppendLine("Output styles:");
        foreach (OutputStyle style in _outputStyleService.List())
        {
            string marker = string.Equals(style.Name, active.Name, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            string source = style.Source == "built-in" ? string.Empty : $" ({style.Source})";
            builder.AppendLine($"{marker} {style.Name}{source} - {style.Description}");
        }

        builder.Append("Switch with /output-style <name>. Add your own as Markdown files in .stemcode/output-styles/.");
        return builder.ToString();
    }
}
