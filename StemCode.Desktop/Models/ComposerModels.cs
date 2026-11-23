using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StemCode.Application.Models;

namespace StemCode.Desktop.Models;

public sealed class ComposerAttachment
{
    public ComposerAttachment(ConversationAttachment attachment, Bitmap? thumbnail, Action<ComposerAttachment> remove)
    {
        Attachment = attachment;
        Thumbnail = thumbnail;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public ConversationAttachment Attachment { get; }

    public Bitmap? Thumbnail { get; }

    public bool HasThumbnail => Thumbnail is not null;

    public bool HasNoThumbnail => Thumbnail is null;

    public string Name => Attachment.Name;

    public string KindText => Attachment.IsImage ? "image" : Attachment.IsText ? "text" : Attachment.MediaType;

    public IRelayCommand RemoveCommand { get; }
}

public enum SuggestionKind
{
    Command,
    File
}

public sealed partial class SuggestionItem : ObservableObject
{
    private static readonly IBrush SelectedBackground = Brush.Parse("#26262B");
    private static readonly IBrush NormalBackground = Brushes.Transparent;

    [ObservableProperty]
    private bool _isSelected;

    public SuggestionItem(SuggestionKind kind, string insertText, string label, string? detail, string? badge, bool requiresArgument, Action<SuggestionItem> accept)
    {
        Kind = kind;
        InsertText = insertText;
        Label = label;
        Detail = detail;
        Badge = badge;
        RequiresArgument = requiresArgument;
        AcceptCommand = new RelayCommand(() => accept(this));
    }

    public SuggestionKind Kind { get; }

    public string InsertText { get; }

    public string Label { get; }

    public string? Detail { get; }

    public string? Badge { get; }

    public bool HasBadge => !string.IsNullOrWhiteSpace(Badge);

    public bool RequiresArgument { get; }

    public bool IsFile => Kind == SuggestionKind.File;

    public bool IsCommand => Kind == SuggestionKind.Command;

    public IBrush Background => IsSelected ? SelectedBackground : NormalBackground;

    public IRelayCommand AcceptCommand { get; }

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(Background));
}

public sealed class QueuedPrompt
{
    public QueuedPrompt(string text, IReadOnlyList<ComposerAttachment> attachments, Action<QueuedPrompt> remove)
    {
        Text = text;
        Attachments = attachments;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Text { get; }

    public IReadOnlyList<ComposerAttachment> Attachments { get; }

    public string Preview => Text.Length > 120 ? Text[..120] + "…" : Text;

    public IRelayCommand RemoveCommand { get; }
}
