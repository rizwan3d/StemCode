using Avalonia.Controls;
using Avalonia.Interactivity;
using StemCode.Desktop.Controls;
using StemCode.Desktop.ViewModels;

namespace StemCode.Desktop.Views;

public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.RequestOpenFolder();
    }

    private void RevealWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        string? path = (DataContext as MainWindowViewModel)?.Project.SelectedProject?.Path;
        if (!string.IsNullOrWhiteSpace(path))
        {
            MarkdownMessageView.OpenPath(path);
        }
    }
}
