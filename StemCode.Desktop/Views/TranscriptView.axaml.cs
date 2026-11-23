using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using StemCode.Desktop.Models;
using StemCode.Desktop.ViewModels;

namespace StemCode.Desktop.Views;

public partial class TranscriptView : UserControl
{
    private const double StickThreshold = 48d;
    private bool _stickToBottom = true;

    public TranscriptView()
    {
        InitializeComponent();
        TranscriptScroll.ScrollChanged += OnScrollChanged;
    }

    /// <summary>Jump to the newest message (used when a session loads or the user sends).</summary>
    public void ScrollToLatest()
    {
        _stickToBottom = true;
        TranscriptScroll.ScrollToEnd();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        ScrollViewer viewer = TranscriptScroll;
        double distanceFromBottom = viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y;

        if (e.ExtentDelta.Y != 0 && _stickToBottom)
        {
            // Content grew (new item or streamed text): follow it.
            viewer.ScrollToEnd();
            return;
        }

        if (e.OffsetDelta.Y != 0)
        {
            _stickToBottom = distanceFromBottom <= StickThreshold;
        }
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TranscriptItem item)
        {
            return;
        }

        IClipboard? clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(item.Content);
        }
    }

    private void Suggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || (sender as Control)?.Tag is not string prompt)
        {
            return;
        }

        if (prompt.StartsWith('/'))
        {
            _ = viewModel.Chat.RunSlashCommand.ExecuteAsync(prompt);
            return;
        }

        viewModel.Chat.Prompt = prompt;
        if (viewModel.Chat.SendCommand.CanExecute(null))
        {
            _ = viewModel.Chat.SendCommand.ExecuteAsync(null);
        }
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.RequestOpenFolder();
    }
}
