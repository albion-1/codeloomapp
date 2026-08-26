using System.Windows;
using System.Windows.Controls;
using codeloomapp.Services;

namespace codeloomapp.Views;

public partial class UnityExportView : UserControl
{
    public event EventHandler? ChangeProjectRequested;
    public event EventHandler? OpenFolderRequested;

    public UnityExportView()
    {
        InitializeComponent();
    }

    public void LoadTarget(string? unityProjectPath, bool isValid)
    {
        if (!isValid || string.IsNullOrWhiteSpace(unityProjectPath))
        {
            TargetText.Text = "No Unity project selected";
            OutputText.Text = $"Generated scripts: {UnityExportService.GeneratedRelativePath}";
            OpenFolderButton.IsEnabled = false;
            return;
        }

        TargetText.Text = unityProjectPath;
        OutputText.Text = $"Generated scripts: {UnityExportService.GeneratedRelativePath}";
        OpenFolderButton.IsEnabled = true;
    }

    public void LoadResult(UnityExportResult result)
    {
        LastExportText.Text = result.Message;

        if (result.Conflicts.Count == 0)
        {
            ConflictNotice.Visibility = Visibility.Collapsed;
            ConflictText.Text = string.Empty;
            return;
        }

        ConflictNotice.Visibility = Visibility.Visible;
        ConflictText.Text = result.Conflicts.Count == 1
            ? "1 generated script has an external edit. Code Loom preserved it."
            : $"{result.Conflicts.Count} generated scripts have external edits. Code Loom preserved them.";
        UnityExpander.IsExpanded = true;
    }

    public void ShowMessage(string message)
    {
        LastExportText.Text = message;
    }

    private void ChangeProject_Click(object sender, RoutedEventArgs e)
    {
        ChangeProjectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenFolderRequested?.Invoke(this, EventArgs.Empty);
    }
}
