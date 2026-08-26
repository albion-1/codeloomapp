using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using codeloomapp.Services;
using codeloomapp.Views;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly UnityExportService _unityExport = new();
    private UnityExportView? _unityExportView;
    private bool _unityExportUiInstalled;
    private bool _unityExportBusy;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureUnityExportUi();
    }

    private void EnsureUnityExportUi()
    {
        if (_unityExportUiInstalled)
            return;

        if (GitHubAccountText.Parent is not Grid statusGrid)
            return;

        StackPanel? statusStack = statusGrid.Parent as StackPanel;
        if (statusStack is null && statusGrid.Parent is Border statusBorder)
        {
            // Fallback for startup timing: preserve the existing status row and
            // turn its border into the same stacked layout used by Git/history.
            statusBorder.Child = null;
            statusStack = new StackPanel();
            statusStack.Children.Add(statusGrid);
            statusBorder.Child = statusStack;
        }

        if (statusStack is null)
            return;

        _unityExportUiInstalled = true;
        _unityExportView = new UnityExportView();
        _unityExportView.ChangeProjectRequested += UnityExportView_ChangeProjectRequested;
        _unityExportView.OpenFolderRequested += UnityExportView_OpenFolderRequested;
        statusStack.Children.Add(_unityExportView);
        RefreshUnityExportUi();
    }

    private void ExportToUnity_Click(object sender, RoutedEventArgs e)
    {
        ExportToUnity();
    }

    private void UnityExportView_ChangeProjectRequested(object? sender, EventArgs e)
    {
        ChooseUnityProject();
    }

    private void UnityExportView_OpenFolderRequested(object? sender, EventArgs e)
    {
        if (!_unityExport.IsUnityProject(_settings.UnityProjectPath))
        {
            StatusText.Text = "Choose a Unity project before opening the generated scripts folder";
            return;
        }

        var generatedRoot = _unityExport.GetGeneratedRoot(_settings.UnityProjectPath);
        Directory.CreateDirectory(generatedRoot);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = generatedRoot,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "Code Loom could not open the generated scripts folder.\n\n" + exception.Message,
                "Unity export",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ExportToUnity()
    {
        if (_unityExportBusy)
            return;

        EnsureUnityExportUi();

        if (!_unityExport.IsUnityProject(_settings.UnityProjectPath)
            && !ChooseUnityProject())
        {
            return;
        }

        CaptureEditorStateForAutosave();
        CommitVariableEdits();

        _unityExportBusy = true;
        SaveStateText.Text = "Exporting to Unity...";
        StatusText.Text = "Assembling Code Loom files into normal Unity C# scripts...";
        _unityExportView?.ShowMessage("Exporting generated scripts...");

        try
        {
            var result = _unityExport.Export(_project, _settings.UnityProjectPath);
            _unityExportView?.LoadResult(result);

            if (!result.Success)
            {
                SaveStateText.Text = "Unity export failed";
                StatusText.Text = "Unity export failed";
                MessageBox.Show(
                    this,
                    result.Message,
                    "Unity export failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SaveStateText.Text = "Exported to Unity";
            StatusText.Text = result.Message;

            // If the Unity project and Code Loom Git repository are the same folder,
            // refresh the Git summary so newly generated scripts appear immediately.
            if (HasRepository()
                && PathsEqual(_settings.GitRepositoryPath, result.UnityProjectPath))
            {
                _ = RefreshGitStatusAsync(fetchRemote: false);
            }

            if (result.Conflicts.Count > 0 || result.Warnings.Count > 0)
                ShowUnityExportNotes(result);
        }
        finally
        {
            _unityExportBusy = false;
        }
    }

    private bool ChooseUnityProject()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the Unity project folder that contains Assets and ProjectSettings"
        };

        if (dialog.ShowDialog(this) != true)
            return false;

        var normalized = _unityExport.NormalizeUnityProjectPath(dialog.FolderName);
        if (normalized is null)
        {
            MessageBox.Show(
                this,
                "That folder does not look like a Unity project.\n\nChoose the project root that contains both the Assets and ProjectSettings folders. You can also choose the Assets folder itself and Code Loom will use its parent project.",
                "Unity project not found",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return false;
        }

        _settings.UnityProjectPath = normalized;
        _storage.SaveSettings(_settings);
        RefreshUnityExportUi();
        StatusText.Text = $"Unity target: {Path.GetFileName(normalized)}";
        return true;
    }

    private void RefreshUnityExportUi()
    {
        _unityExportView?.LoadTarget(
            _settings.UnityProjectPath,
            _unityExport.IsUnityProject(_settings.UnityProjectPath));
    }

    private void ShowUnityExportNotes(UnityExportResult result)
    {
        var lines = new List<string>();

        if (result.Conflicts.Count > 0)
        {
            lines.Add("External edits preserved:");
            lines.AddRange(result.Conflicts
                .Take(6)
                .Select(conflict => $"• {conflict.RelativePath}\n  {conflict.Reason}"));

            if (result.Conflicts.Count > 6)
                lines.Add($"• …and {result.Conflicts.Count - 6} more");
        }

        if (result.Warnings.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add(string.Empty);
            lines.Add("Export notes:");
            lines.AddRange(result.Warnings.Take(6).Select(warning => "• " + warning));
            if (result.Warnings.Count > 6)
                lines.Add($"• …and {result.Warnings.Count - 6} more");
        }

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            result.Conflicts.Count > 0 ? "Unity export preserved external edits" : "Unity export notes",
            MessageBoxButton.OK,
            result.Conflicts.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private static bool PathsEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}
