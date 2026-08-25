using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;

namespace codeloomapp;

public partial class MainWindow
{
    private void FileActionsPanel_Loaded(object sender, RoutedEventArgs e)
    {
        ProjectTree.SelectedItemChanged -= ProjectTree_FileActionVisibilityChanged;
        ProjectTree.SelectedItemChanged += ProjectTree_FileActionVisibilityChanged;
        UpdateFileActionVisibility();
    }

    private void ProjectTree_FileActionVisibilityChanged(
        object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        UpdateFileActionVisibility();
    }

    private void UpdateFileActionVisibility()
    {
        var hasSelectedFile = ProjectTree.SelectedItem is TreeViewItem item
                              && item.Tag is CodeFile;

        FileActionsPanel.Visibility = hasSelectedFile
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
