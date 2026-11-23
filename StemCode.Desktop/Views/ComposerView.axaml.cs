using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StemCode.Desktop.Models;
using StemCode.Desktop.ViewModels;

namespace StemCode.Desktop.Views;

public partial class ComposerView : UserControl
{
    private const double SuggestionRowHeight = 30d;
    private ChatViewModel? _chat;

    public ComposerView()
    {
        InitializeComponent();
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);
        ComposerFrame.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        ComposerFrame.AddHandler(DragDrop.DropEvent, OnDrop);
        ComposerFrame.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropHighlight(false));
    }

    public void FocusPrompt()
    {
        PromptBox.Focus();
        PromptBox.CaretIndex = PromptBox.Text?.Length ?? 0;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_chat is not null)
        {
            _chat.CaretRequested -= OnCaretRequested;
            _chat.SuggestionSelectionChanged -= OnSuggestionSelectionChanged;
            _chat.PropertyChanged -= OnChatPropertyChanged;
        }

        _chat = (DataContext as MainWindowViewModel)?.Chat;
        if (_chat is not null)
        {
            _chat.CaretRequested += OnCaretRequested;
            _chat.SuggestionSelectionChanged += OnSuggestionSelectionChanged;
            _chat.PropertyChanged += OnChatPropertyChanged;
        }
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatViewModel.HasInlineTextPrompt) && _chat?.HasInlineTextPrompt == true)
        {
            Dispatcher.UIThread.Post(() => InlineTextPromptBox.Focus(), DispatcherPriority.Input);
        }
    }

    private void OnCaretRequested(object? sender, int caret)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PromptBox.Focus();
            PromptBox.CaretIndex = Math.Clamp(caret, 0, PromptBox.Text?.Length ?? 0);
        }, DispatcherPriority.Input);
    }

    private void OnSuggestionSelectionChanged(object? sender, SuggestionItem item)
    {
        if (_chat is null)
        {
            return;
        }

        int index = _chat.Suggestions.IndexOf(item);
        double top = index * SuggestionRowHeight;
        double viewport = SuggestionScroll.Viewport.Height;
        double offset = SuggestionScroll.Offset.Y;
        if (top < offset)
        {
            SuggestionScroll.Offset = new Vector(0, top);
        }
        else if (top + SuggestionRowHeight > offset + viewport)
        {
            SuggestionScroll.Offset = new Vector(0, top + SuggestionRowHeight - viewport);
        }
    }

    private async void OnPromptKeyDown(object? sender, KeyEventArgs e)
    {
        if (_chat is null)
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.R)
        {
            e.Handled = true;
            await _chat.DictateVoiceAsync();
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            // Let the TextBox paste text as usual; images and copied files become attachments.
            _ = TryPasteAttachmentsAsync();
            return;
        }

        // Keep the view-model caret in sync before it inspects the input.
        _chat.PromptCaretIndex = PromptBox.CaretIndex;
        if (_chat.HandleComposerKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private async Task TryPasteAttachmentsAsync()
    {
        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null || _chat is null)
        {
            return;
        }

        try
        {
            string? text = await clipboard.TryGetTextAsync();
            if (!string.IsNullOrEmpty(text))
            {
                return;
            }

            IStorageItem[]? files = await clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                AddStorageItems(files);
                return;
            }

            Bitmap? bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                using MemoryStream stream = new();
                bitmap.Save(stream);
                _chat.AddImageAttachment(stream.ToArray(), bitmap);
            }
        }
        catch (Exception exception)
        {
            _chat.AddNotice($"Could not paste from the clipboard: {exception.Message}", isError: true);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFiles = e.DataTransfer.Contains(DataFormat.File);
        e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropHighlight(hasFiles);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropHighlight(false);
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        if (items is { Length: > 0 })
        {
            AddStorageItems(items);
            e.Handled = true;
        }
    }

    private void AddStorageItems(IEnumerable<IStorageItem> items)
    {
        if (_chat is null)
        {
            return;
        }

        foreach (IStorageItem item in items)
        {
            string? path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                // Folders become @-mentions so the agent can explore them.
                string root = _chat.ActiveProject?.Path ?? string.Empty;
                string display = root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetRelativePath(root, path).Replace('\\', '/')
                    : path;
                _chat.Prompt = (_chat.Prompt.Length > 0 && !_chat.Prompt.EndsWith(' ') ? _chat.Prompt + " " : _chat.Prompt) + "@" + display + " ";
                continue;
            }

            _chat.AddAttachmentFromFile(path);
        }

        FocusPrompt();
    }

    private void SetDropHighlight(bool active)
    {
        ComposerFrame.BorderBrush = active
            ? (Avalonia.Media.IBrush)this.FindResource("Accent")!
            : (Avalonia.Media.IBrush)this.FindResource("BorderDefault")!;
    }

    private async void Attach_Click(object? sender, RoutedEventArgs e)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null || _chat is null)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true
        });
        AddStorageItems(files);
    }

    private async void Voice_Click(object? sender, RoutedEventArgs e)
    {
        if (_chat is not null)
        {
            await _chat.DictateVoiceAsync();
            FocusPrompt();
        }
    }

    private void TextPromptBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _chat?.ActiveTextPrompt is { } prompt)
        {
            prompt.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
