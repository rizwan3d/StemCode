using Avalonia.Media;
using StemCode.Application.Models;

namespace StemCode.Desktop.Models;

public enum PlanStepState
{
    Pending,
    Current,
    Done
}

public sealed record PlanStep(string Text, PlanStepState State)
{
    private static readonly IBrush DoneBrush = Brush.Parse("#4ADE80");
    private static readonly IBrush CurrentBrush = Brush.Parse("#FBBF24");
    private static readonly IBrush PendingBrush = Brush.Parse("#71717A");
    private static readonly IBrush DoneText = Brush.Parse("#8B8B93");
    private static readonly IBrush ActiveText = Brush.Parse("#ECECEE");

    public string Glyph => State switch
    {
        PlanStepState.Done => "✓",
        PlanStepState.Current => "◐",
        _ => "○"
    };

    public IBrush GlyphBrush => State switch
    {
        PlanStepState.Done => DoneBrush,
        PlanStepState.Current => CurrentBrush,
        _ => PendingBrush
    };

    public IBrush TextBrush => State == PlanStepState.Done ? DoneText : ActiveText;

    public TextDecorationCollection? Decorations => State == PlanStepState.Done ? TextDecorations.Strikethrough : null;

    public static IReadOnlyList<PlanStep> From(ExecutionPlanProgress progress)
    {
        List<PlanStep> steps = new(progress.Tasks.Count);
        for (int index = 0; index < progress.Tasks.Count; index++)
        {
            PlanStepState state = index < progress.CompletedTaskCount
                ? PlanStepState.Done
                : index == progress.CurrentTaskIndex ? PlanStepState.Current : PlanStepState.Pending;
            steps.Add(new PlanStep(progress.Tasks[index], state));
        }

        return steps;
    }
}
