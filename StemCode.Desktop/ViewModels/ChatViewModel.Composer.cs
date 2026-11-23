using Avalonia.Input;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using StemCode.Application.Abstractions;
using StemCode.Application.Models;
using StemCode.Desktop.Models;
using StemCode.Desktop.Services;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace StemCode.Desktop.ViewModels;

public partial class ChatViewModel
{
    private const int MaxSuggestions = 10;
    private const int MaxHistory = 200;

    private readonly WorkspaceFileIndex _fileIndex = new();
    private readonly List<string> _promptHistory = [];
    private IReadOnlyList<McpPromptDescriptor> _mcpPrompts = [];
    private IReadOnlyList<DesktopCommandInfo>? _commandCache;
    private string? _commandCacheRoot;
    private int _historyIndex = -1;
    private string? _historyDraft;
    private bool _suggestionsDismissed;
    private bool _isApplyingCompletion;
    private int _selectedSuggestionIndex;
    private int _completionStart;
    private int _completionEnd;

    [ObservableProperty]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private int _promptCaretIndex;

    public ObservableCollection<ComposerAttachment> ComposerAttachments { get; } = new();

    public ObservableCollection<SuggestionItem> Suggestions { get; } = new();

    public ObservableCollection<QueuedPrompt> QueuedPrompts { get; } = new();

    public bool HasAttachments => ComposerAttachments.Count > 0;

    public bool HasSuggestions => Suggestions.Count > 0;

    public bool HasQueuedPrompts => QueuedPrompts.Count > 0;

    public string SuggestionHeader => Suggestions.FirstOrDefault()?.IsFile == true ? "Files" : "Commands";

    public string ComposerPlaceholder => ActiveProject is null
        ? "Open a workspace folder to start"
        : IsRunning
            ? "Queue a follow-up… (sent when the current task finishes)"
            : "Ask StemCode anything — / for commands, @ for files, ! for shell";

    /// <summary>Raised when the view should move the caret (after a completion or history recall).</summary>
    public event EventHandler<int>? CaretRequested;

    /// <summary>All commands the composer can complete, for the command palette as well.</summary>
    public IReadOnlyList<DesktopCommandInfo> GetCommandCatalog()
    {
        string? root = ActiveProject?.Path;
        if (_commandCache is null || !string.Equals(_commandCacheRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            _commandCache = DesktopCommandCatalog.GetAll(root, _mcpPrompts);
            _commandCacheRoot = root;
        }

        return _commandCache;
    }

    private void InitializeComposer()
    {
        ComposerAttachments.CollectionChanged += OnComposerAttachmentsChanged;
        QueuedPrompts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasQueuedPrompts));
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsRunning) or nameof(ActiveProject))
            {
                OnPropertyChanged(nameof(ComposerPlaceholder));
            }
        };
    }

    private void OnComposerAttachmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnComposerProjectChanged()
    {
        _mcpPrompts = [];
        _commandCache = null;
        _fileIndex.Invalidate();
        DismissSuggestions();
    }

    private async Task RefreshMcpPromptsAsync()
    {
        IReadOnlyList<McpPromptDescriptor> prompts = await Task.Run(() => _agentRunner.ListMcpPrompts());
        _mcpPrompts = prompts;
        _commandCache = null;
    }

    partial void OnPromptChanged(string value)
    {
        SendCommand.NotifyCanExecuteChanged();
        if (!_isApplyingCompletion)
        {
            _suggestionsDismissed = false;
        }

        RefreshSuggestions();
    }

    partial void OnPromptCaretIndexChanged(int value)
    {
        if (!_isApplyingCompletion)
        {
            RefreshSuggestions();
        }
    }

    // ---------------------------------------------------------------- attachments

    public void AddAttachmentFromFile(string path)
    {
        if (!AttachmentFactory.TryCreateFromFile(path, out ConversationAttachment? attachment, out string? error) || attachment is null)
        {
            AddNotice(error ?? $"Could not attach {path}.", isError: true);
            return;
        }

        Bitmap? thumbnail = null;
        if (attachment.IsImage)
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                thumbnail = Bitmap.DecodeToWidth(stream, 96);
            }
            catch (Exception)
            {
                thumbnail = null;
            }
        }

        AddAttachment(attachment, thumbnail);
    }

    public void AddImageAttachment(byte[] pngBytes, Bitmap? thumbnail)
    {
        int index = ComposerAttachments.Count(static item => item.Attachment.IsImage) + 1;
        AddAttachment(AttachmentFactory.CreateImage($"pasted-image-{index}.png", pngBytes), thumbnail);
    }

    private void AddAttachment(ConversationAttachment attachment, Bitmap? thumbnail)
    {
        if (ComposerAttachments.Any(existing =>
                string.Equals(existing.Name, attachment.Name, StringComparison.OrdinalIgnoreCase) &&
                existing.Attachment.ContentBase64.Length == attachment.ContentBase64.Length))
        {
            return;
        }

        ComposerAttachments.Add(new ComposerAttachment(attachment, thumbnail, item => ComposerAttachments.Remove(item)));
    }

    // ---------------------------------------------------------------- history

    private void RememberPrompt(string text)
    {
        _historyIndex = -1;
        _historyDraft = null;
        if (_promptHistory.Count > 0 && string.Equals(_promptHistory[^1], text, StringComparison.Ordinal))
        {
            return;
        }

        _promptHistory.Add(text);
        if (_promptHistory.Count > MaxHistory)
        {
            _promptHistory.RemoveAt(0);
        }
    }

    private bool TryRecallHistory(int direction)
    {
        if (_promptHistory.Count == 0)
        {
            return false;
        }

        if (_historyIndex == -1)
        {
            if (direction > 0)
            {
                return false;
            }

            _historyDraft = Prompt;
            _historyIndex = _promptHistory.Count;
        }

        int next = _historyIndex + direction;
        if (next < 0)
        {
            return true;
        }

        string value;
        if (next >= _promptHistory.Count)
        {
            _historyIndex = -1;
            value = _historyDraft ?? string.Empty;
        }
        else
        {
            _historyIndex = next;
            value = _promptHistory[next];
        }

        _isApplyingCompletion = true;
        try
        {
            Prompt = value;
            _suggestionsDismissed = true;
            DismissSuggestions();
        }
        finally
        {
            _isApplyingCompletion = false;
        }

        CaretRequested?.Invoke(this, value.Length);
        return true;
    }

    // ---------------------------------------------------------------- keys

    /// <summary>Handles composer keys. Returns true when the key was consumed.</summary>
    public bool HandleComposerKey(Key key, KeyModifiers modifiers)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);

        if (HasSuggestions)
        {
            switch (key)
            {
                case Key.Up:
                    MoveSuggestion(-1);
                    return true;
                case Key.Down:
                    MoveSuggestion(1);
                    return true;
                case Key.Tab:
                    AcceptSuggestion(Suggestions[_selectedSuggestionIndex], submit: false);
                    return true;
                case Key.Enter when !shift && !ctrl:
                    AcceptSuggestion(Suggestions[_selectedSuggestionIndex], submit: true);
                    return true;
                case Key.Escape:
                    _suggestionsDismissed = true;
                    DismissSuggestions();
                    return true;
            }
        }

        if (key == Key.Enter && !shift)
        {
            if (SendCommand.CanExecute(null))
            {
                _ = SendCommand.ExecuteAsync(null);
            }

            return true;
        }

        if (key == Key.Up && !shift && !ctrl && (PromptCaretIndex == 0 || !Prompt.Contains('\n') || _historyIndex >= 0))
        {
            return TryRecallHistory(-1);
        }

        if (key == Key.Down && !shift && !ctrl && _historyIndex >= 0)
        {
            return TryRecallHistory(1);
        }

        return false;
    }

    // ---------------------------------------------------------------- suggestions

    private void RefreshSuggestions()
    {
        if (_suggestionsDismissed || ActiveProject is null)
        {
            DismissSuggestions();
            return;
        }

        string text = Prompt ?? string.Empty;
        int caret = Math.Clamp(PromptCaretIndex, 0, text.Length);
        string before = text[..caret];

        List<SuggestionItem> items = [];
        if (before.StartsWith('/') && !before.Contains('\n') && (before.IndexOf(' ') < 0 || before.StartsWith("/voice ", StringComparison.OrdinalIgnoreCase)))
        {
            _completionStart = 0;
            _completionEnd = caret;
            IReadOnlyList<DesktopCommandInfo> catalog = GetCommandCatalog();
            IEnumerable<DesktopCommandInfo> prefix = catalog.Where(command => command.Command.StartsWith(before, StringComparison.OrdinalIgnoreCase));
            string needle = before.TrimStart('/');
            IEnumerable<DesktopCommandInfo> fuzzy = needle.Length < 3
                ? []
                : catalog.Where(command =>
                    !command.Command.StartsWith(before, StringComparison.OrdinalIgnoreCase) &&
                    (command.Command.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                     command.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)));

            foreach (DesktopCommandInfo command in prefix.Concat(fuzzy).Take(MaxSuggestions * 3))
            {
                items.Add(new SuggestionItem(
                    SuggestionKind.Command,
                    command.Command,
                    command.Usage,
                    command.Description,
                    command.SourceLabel,
                    command.RequiresArgument,
                    item => AcceptSuggestion(item, submit: false)));
            }

            if (items.Count == 1 && string.Equals(items[0].InsertText, before, StringComparison.OrdinalIgnoreCase))
            {
                items.Clear();
            }
        }
        else
        {
            int tokenStart = before.LastIndexOfAny([' ', '\n', '\t', '(', '"', '\'']) + 1;
            string token = before[tokenStart..];
            if (token.StartsWith('@'))
            {
                _completionStart = tokenStart;
                _completionEnd = caret;
                foreach (string path in _fileIndex.Search(ActiveProject.Path, token[1..], MaxSuggestions * 2))
                {
                    string directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? string.Empty;
                    items.Add(new SuggestionItem(
                        SuggestionKind.File,
                        "@" + path,
                        Path.GetFileName(path),
                        directory.Length == 0 ? "./" : directory,
                        null,
                        requiresArgument: false,
                        item => AcceptSuggestion(item, submit: false)));
                }
            }
        }

        Suggestions.Clear();
        foreach (SuggestionItem item in items)
        {
            Suggestions.Add(item);
        }

        _selectedSuggestionIndex = 0;
        if (Suggestions.Count > 0)
        {
            Suggestions[0].IsSelected = true;
        }

        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(SuggestionHeader));
    }

    private void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        Suggestions[_selectedSuggestionIndex].IsSelected = false;
        _selectedSuggestionIndex = (_selectedSuggestionIndex + delta + Suggestions.Count) % Suggestions.Count;
        Suggestions[_selectedSuggestionIndex].IsSelected = true;
        SuggestionSelectionChanged?.Invoke(this, Suggestions[_selectedSuggestionIndex]);
    }

    /// <summary>Raised so the view can scroll the selected suggestion into view.</summary>
    public event EventHandler<SuggestionItem>? SuggestionSelectionChanged;

    private void AcceptSuggestion(SuggestionItem item, bool submit)
    {
        string text = Prompt ?? string.Empty;
        int start = Math.Clamp(_completionStart, 0, text.Length);
        int end = Math.Clamp(_completionEnd, start, text.Length);
        string insert = item.InsertText;
        bool addSpace = item.IsFile || item.RequiresArgument;
        if (addSpace && (end >= text.Length || text[end] != ' '))
        {
            insert += " ";
        }

        string updated = text[..start] + insert + text[end..];
        int caret = start + insert.Length;

        _isApplyingCompletion = true;
        try
        {
            Prompt = updated;
            PromptCaretIndex = caret;
            _suggestionsDismissed = true;
            DismissSuggestions();
        }
        finally
        {
            _isApplyingCompletion = false;
        }

        CaretRequested?.Invoke(this, caret);

        if (submit && item.IsCommand && !item.RequiresArgument && SendCommand.CanExecute(null))
        {
            _ = SendCommand.ExecuteAsync(null);
        }
    }

    public void DismissSuggestions()
    {
        if (Suggestions.Count == 0)
        {
            return;
        }

        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
    }
}
