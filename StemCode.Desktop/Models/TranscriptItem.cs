using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text;

namespace StemCode.Desktop.Models;

public enum TranscriptKind
{
    User,
    Assistant,
    Thinking,
    Tool,
    Plan,
    Notice,
    Error
}

public enum ToolRunStatus
{
    None,
    Running,
    Succeeded,
    Failed
}

/// <summary>
/// One entry in the conversation transcript. Mutable so streamed assistant text,
/// tool results, and run metrics can update an entry after it has been rendered.
/// </summary>
public sealed partial class TranscriptItem : ObservableObject
{
    private const int MaxRenderedToolLines = 400;

    private static readonly IBrush RunningBrush = Brush.Parse("#FBBF24");
    private static readonly IBrush SucceededBrush = Brush.Parse("#4ADE80");
    private static readonly IBrush FailedBrush = Brush.Parse("#F87171");
    private static readonly IBrush NeutralBrush = Brush.Parse("#71717A");

    private readonly StringBuilder _streamBuffer = new();
    private bool _hasPendingStream;
    private IReadOnlyList<ToolOutputLine>? _outputLines;
    private int? _turnNumber;

    [ObservableProperty]
    private string _content;

    [ObservableProperty]
    private string? _title;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string? _statusNote;

    [ObservableProperty]
    private ToolRunStatus _status;

    [ObservableProperty]
    private IReadOnlyList<PlanStep> _steps = [];

    public TranscriptItem(
        TranscriptKind kind,
        string content,
        string? workspacePath = null,
        string? title = null,
        IReadOnlyList<string>? attachmentNames = null)
    {
        Kind = kind;
        _content = content ?? string.Empty;
        _title = title;
        WorkspacePath = workspacePath;
        AttachmentNames = attachmentNames ?? [];
        Timestamp = DateTimeOffset.Now;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public TranscriptKind Kind { get; }

    public string? WorkspacePath { get; }

    public DateTimeOffset Timestamp { get; }

    public string? ToolCallId { get; init; }

    public IReadOnlyList<string> AttachmentNames { get; }

    /// <summary>1-based user turn number, used by fork and rewind actions.</summary>
    public int? TurnNumber
    {
        get => _turnNumber;
        set
        {
            if (SetProperty(ref _turnNumber, value))
            {
                OnPropertyChanged(nameof(HasTurnNumber));
            }
        }
    }

    public IRelayCommand ToggleExpandedCommand { get; }

    public bool IsUser => Kind == TranscriptKind.User;

    public bool IsAssistant => Kind == TranscriptKind.Assistant;

    public bool IsThinking => Kind == TranscriptKind.Thinking;

    public bool IsTool => Kind == TranscriptKind.Tool;

    public bool IsPlan => Kind == TranscriptKind.Plan;

    public bool IsNotice => Kind == TranscriptKind.Notice;

    public bool IsError => Kind == TranscriptKind.Error;

    public bool IsCard => Kind is TranscriptKind.Thinking or TranscriptKind.Tool;

    public bool HasStatusNote => !string.IsNullOrWhiteSpace(StatusNote);

    public bool HasAttachments => AttachmentNames.Count > 0;

    public string AttachmentsText => string.Join("  ·  ", AttachmentNames);

    public bool HasTurnNumber => TurnNumber is not null;

    public string TimeText => Timestamp.ToString("h:mm tt");

    public string CardTitle => Kind switch
    {
        TranscriptKind.Thinking => IsStreaming ? "Thinking…" : "Thought process",
        _ => string.IsNullOrWhiteSpace(Title) ? "Tool" : Title!
    };

    /// <summary>First meaningful line, shown beside a collapsed card.</summary>
    public string Preview
    {
        get
        {
            if (Kind == TranscriptKind.Thinking)
            {
                string flat = Content.Replace("\r", " ").Replace("\n", " ").Trim();
                return flat.Length > 140 ? flat[..140] + "…" : flat;
            }

            return Status switch
            {
                ToolRunStatus.Running => "running",
                ToolRunStatus.Failed => "failed",
                _ => SummarizeToolOutput()
            };
        }
    }

    public IBrush StatusBrush => Status switch
    {
        ToolRunStatus.Running => RunningBrush,
        ToolRunStatus.Succeeded => SucceededBrush,
        ToolRunStatus.Failed => FailedBrush,
        _ => NeutralBrush
    };

    public bool IsRunning => Status == ToolRunStatus.Running;

    public bool HasExpandableContent => !string.IsNullOrWhiteSpace(Content);

    public string ChevronGlyph => IsExpanded ? "▾" : "▸";

    /// <summary>Tool output split into lines with diff colouring, capped for render cost.</summary>
    public IReadOnlyList<ToolOutputLine> OutputLines => _outputLines ??= ToolOutputLine.Parse(Content, MaxRenderedToolLines);

    public void AppendStream(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (_streamBuffer.Length == 0 && Content.Length > 0)
        {
            _streamBuffer.Append(Content);
        }

        _streamBuffer.Append(chunk);
        _hasPendingStream = true;
    }

    /// <summary>Pushes buffered stream text to <see cref="Content"/>. Returns true when anything changed.</summary>
    public bool FlushStream()
    {
        if (!_hasPendingStream)
        {
            return false;
        }

        _hasPendingStream = false;
        Content = _streamBuffer.ToString();
        return true;
    }

    public void ReplaceContent(string content)
    {
        _streamBuffer.Clear();
        _hasPendingStream = false;
        Content = content ?? string.Empty;
    }

    partial void OnContentChanged(string value)
    {
        _outputLines = null;
        OnPropertyChanged(nameof(OutputLines));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasExpandableContent));
    }

    partial void OnTitleChanged(string? value)
    {
        OnPropertyChanged(nameof(CardTitle));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ChevronGlyph));
    }

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(CardTitle));
    }

    partial void OnStatusNoteChanged(string? value)
    {
        OnPropertyChanged(nameof(HasStatusNote));
    }

    public string PlanSummary => Steps.Count == 0
        ? "Plan"
        : $"Plan · {Steps.Count(static step => step.State == PlanStepState.Done)}/{Steps.Count} done";

    partial void OnStepsChanged(IReadOnlyList<PlanStep> value)
    {
        OnPropertyChanged(nameof(PlanSummary));
    }

    partial void OnStatusChanged(ToolRunStatus value)
    {
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(Preview));
    }

    private string SummarizeToolOutput()
    {
        int added = 0;
        int removed = 0;
        int lines = 0;
        foreach (ToolOutputLine line in OutputLines)
        {
            lines++;
            if (line.Kind == ToolOutputLineKind.Added)
            {
                added++;
            }
            else if (line.Kind == ToolOutputLineKind.Removed)
            {
                removed++;
            }
        }

        if (added > 0 || removed > 0)
        {
            return $"+{added} −{removed}";
        }

        return lines <= 1 ? string.Empty : $"{lines} lines";
    }
}

public enum ToolOutputLineKind
{
    Plain,
    Header,
    Added,
    Removed,
    Muted
}

public sealed record ToolOutputLine(string Text, ToolOutputLineKind Kind)
{
    private static readonly IBrush PlainBrush = Brush.Parse("#C9C9CF");
    private static readonly IBrush HeaderBrush = Brush.Parse("#ECECEE");
    private static readonly IBrush AddedBrush = Brush.Parse("#86EFAC");
    private static readonly IBrush RemovedBrush = Brush.Parse("#FCA5A5");
    private static readonly IBrush MutedBrush = Brush.Parse("#71717A");
    private static readonly IBrush AddedBackground = Brush.Parse("#0F2A1A");
    private static readonly IBrush RemovedBackground = Brush.Parse("#2E1414");
    private static readonly IBrush NoBackground = Brushes.Transparent;

    public IBrush Foreground => Kind switch
    {
        ToolOutputLineKind.Header => HeaderBrush,
        ToolOutputLineKind.Added => AddedBrush,
        ToolOutputLineKind.Removed => RemovedBrush,
        ToolOutputLineKind.Muted => MutedBrush,
        _ => PlainBrush
    };

    public IBrush Background => Kind switch
    {
        ToolOutputLineKind.Added => AddedBackground,
        ToolOutputLineKind.Removed => RemovedBackground,
        _ => NoBackground
    };

    public static IReadOnlyList<ToolOutputLine> Parse(string? text, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        string[] rawLines = text.Replace("\r\n", "\n").Split('\n');
        bool isDiff = LooksLikeDiff(text);
        int count = Math.Min(rawLines.Length, maxLines);
        List<ToolOutputLine> lines = new(count + 1);
        for (int index = 0; index < count; index++)
        {
            ToolOutputLineKind kind = isDiff ? Classify(rawLines[index]) : ToolOutputLineKind.Plain;
            lines.Add(new ToolOutputLine(rawLines[index], kind));
        }

        if (rawLines.Length > count)
        {
            lines.Add(new ToolOutputLine($"… {rawLines.Length - count} more lines", ToolOutputLineKind.Muted));
        }

        return lines;
    }

    public static bool LooksLikeDiff(string text)
    {
        return text.StartsWith("diff --git", StringComparison.Ordinal) ||
            text.Contains("\n@@ ", StringComparison.Ordinal) ||
            text.StartsWith("@@ ", StringComparison.Ordinal) ||
            text.Contains("• Edited ", StringComparison.Ordinal) ||
            text.Contains("• Created ", StringComparison.Ordinal) ||
            text.Contains("• Deleted ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Recognises both unified diffs ("+foo") and StemCode's numbered edit previews
    /// ("      12 + foo"), plus the "• Edited" / "  - path (+a -b)" headers.
    /// </summary>
    public static ToolOutputLineKind Classify(string line)
    {
        if (line.StartsWith("• ", StringComparison.Ordinal) ||
            line.StartsWith("diff --git", StringComparison.Ordinal) ||
            line.StartsWith("@@", StringComparison.Ordinal) ||
            line.StartsWith("  - ", StringComparison.Ordinal))
        {
            return ToolOutputLineKind.Header;
        }

        if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
        {
            return ToolOutputLineKind.Muted;
        }

        if (line.StartsWith("    ... ", StringComparison.Ordinal))
        {
            return ToolOutputLineKind.Muted;
        }

        if (line.Length > 0 && line[0] == '+')
        {
            return ToolOutputLineKind.Added;
        }

        if (line.Length > 0 && line[0] == '-')
        {
            return ToolOutputLineKind.Removed;
        }

        // Numbered preview: leading spaces, digits, a space, then +/-.
        int index = 0;
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        int digitsStart = index;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        if (index > digitsStart && index + 1 < line.Length && line[index] == ' ')
        {
            char marker = line[index + 1];
            if (marker == '+')
            {
                return ToolOutputLineKind.Added;
            }

            if (marker == '-')
            {
                return ToolOutputLineKind.Removed;
            }
        }

        return ToolOutputLineKind.Plain;
    }
}
