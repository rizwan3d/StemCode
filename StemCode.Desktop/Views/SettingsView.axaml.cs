using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StemCode.Desktop.ViewModels;

namespace StemCode.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void Section_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && (sender as Control)?.DataContext is SettingsSection section)
        {
            viewModel.Settings.SelectedSection = section;
        }
    }

    private void CustomCommand_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel viewModel && viewModel.Settings.RunCustomCommand.CanExecute(null))
        {
            _ = viewModel.Settings.RunCustomCommand.ExecuteAsync(null);
            e.Handled = true;
        }
    }
}
