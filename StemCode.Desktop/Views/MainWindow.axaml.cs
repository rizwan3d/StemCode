using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using StemCode.Desktop.Models;
using StemCode.Desktop.ViewModels;

namespace StemCode.Desktop.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => Composer.FocusPrompt();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.OpenFolderRequested -= OnOpenFolderRequested;
            _viewModel.FocusComposerRequested -= OnFocusComposerRequested;
            _viewModel.Palette.PropertyChanged -= OnPalettePropertyChanged;
            _viewModel.Palette.SelectionChanged -= OnPaletteSelectionChanged;
            _viewModel.Chat.SessionSwitched -= OnSessionSwitched;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.OpenFolderRequested += OnOpenFolderRequested;
            _viewModel.FocusComposerRequested += OnFocusComposerRequested;
            _viewModel.Palette.PropertyChanged += OnPalettePropertyChanged;
            _viewModel.Palette.SelectionChanged += OnPaletteSelectionChanged;
            _viewModel.Chat.SessionSwitched += OnSessionSwitched;
        }
    }

    private void OnSessionSwitched(object? sender, string e) => Transcript.ScrollToLatest();

    private void OnFocusComposerRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => Composer.FocusPrompt(), DispatcherPriority.Input);
    }

    private void OnPalettePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen))
        {
            if (_viewModel?.Palette.IsOpen == true)
            {
                Dispatcher.UIThread.Post(() => PaletteBox.Focus(), DispatcherPriority.Input);
            }
            else
            {
                OnFocusComposerRequested(this, EventArgs.Empty);
            }
        }
    }

    private void OnPaletteSelectionChanged(object? sender, PaletteEntry entry)
    {
        if (_viewModel is null)
        {
            return;
        }

        const double rowHeight = 34d;
        int index = _viewModel.Palette.Results.IndexOf(entry);
        double top = index * rowHeight;
        double viewport = PaletteScroll.Viewport.Height;
        if (top < PaletteScroll.Offset.Y)
        {
            PaletteScroll.Offset = new Vector(0, top);
        }
        else if (top + rowHeight > PaletteScroll.Offset.Y + viewport)
        {
            PaletteScroll.Offset = new Vector(0, top + rowHeight - viewport);
        }
    }

    private async void OnOpenFolderRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open workspace folder",
            AllowMultiple = false
        });

        string? path = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            await _viewModel.Project.AddProjectAsync(path);
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Palette owns the keyboard while open.
        if (_viewModel.Palette.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    _viewModel.Palette.Close();
                    e.Handled = true;
                    return;
                case Key.Down:
                    _viewModel.Palette.Move(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.Palette.Move(-1);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    _viewModel.Palette.ExecuteSelected();
                    e.Handled = true;
                    return;
            }

            if (!(ctrl && e.Key == Key.K))
            {
                return;
            }
        }

        if (ctrl && (e.Key == Key.K || (shift && e.Key == Key.P)))
        {
            if (_viewModel.Palette.IsOpen)
            {
                _viewModel.Palette.Close();
            }
            else
            {
                _viewModel.Palette.Open();
            }

            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.OemComma)
        {
            _viewModel.OpenSettingsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.N)
        {
            _ = _viewModel.NewSessionCommand.ExecuteAsync(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.O)
        {
            _viewModel.RequestOpenFolder();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.B)
        {
            _viewModel.ToggleSidebarCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && (e.Key == Key.J || (shift && e.Key == Key.I)))
        {
            _viewModel.ToggleInspectorCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.L)
        {
            Composer.FocusPrompt();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsSettingsOpen && !_viewModel.ShowModalPrompt)
            {
                _viewModel.IsSettingsOpen = false;
                e.Handled = true;
                return;
            }

            if (_viewModel.Chat.HandleEscape())
            {
                e.Handled = true;
            }

            return;
        }

        // Number keys answer an approval prompt unless the user is typing in a text field.
        DesktopSelectionPrompt? prompt = _viewModel.Chat.ActiveSelectionPrompt;
        if (prompt is not null && !ctrl && TryGetDigit(e.Key, out int digit))
        {
            bool typingInText = FocusManager?.GetFocusedElement() is TextBox textBox &&
                !string.IsNullOrEmpty(textBox.Text);
            if (!typingInText && prompt.TrySelectNumber(digit))
            {
                e.Handled = true;
            }
        }
    }

    private static bool TryGetDigit(Key key, out int digit)
    {
        digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => 0
        };
        return digit > 0;
    }

    private void PaletteBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _viewModel?.Palette.Close();
    }

    private void PaletteBox_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Keep clicks inside the palette from closing it.
        e.Handled = true;
    }

    private void OnboardingTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel?.Chat.ActiveTextPrompt is { } prompt)
        {
            prompt.SubmitCommand.Execute(null);
            e.Handled = true;
        }
    }
}
