using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using codeloomapp.Services;
using codeloomapp.Views;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly UnityExportService _unityExport = new();
    private UnityExportView? _unityExportView;
    private bool _unityExportUiInstalled;
    private bool _unityExportToolbarInstalled;
    private bool _unityExportBusy;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureUnityExportUi();
        InstallUnityExportToolbarButton();

        // If startup ordering caused the Git/status stack to be installed a moment
        // later, a future activation gets another harmless chance to attach the
        // collapsed Unity details panel. The toolbar export remains available either way.
        Activated += (_, _) =>
        {
            EnsureUnityExportUi();
            RefreshUnityExportUi();
        };
    }

    private void EnsureUnityExportUi()
    {
        if (_unityExportUiInstalled)
            return;

        if (GitHubAccountText.Parent is not Grid statusGrid
            || statusGrid.Parent is not StackPanel statusStack)
        {
            return;
        }

        _unityExportUiInstalled = true;
        _unityExportView = new UnityExportView();
        _unityExportView.ChangeProjectRequested += UnityExportView_ChangeProjectRequested;
        _unityExportView.OpenFolderRequested += UnityExportView_OpenFolderRequested;
        statusStack.Children.Add(_unityExportView);
        RefreshUnityExportUi();
    }

    private void InstallUnityExportToolbarButton()
    {
        if (_unityExportToolbarInstalled || Content is not Grid root)
            return;

        var topRow = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 0);
        var toolbar = topRow?.Children.OfType<WrapPanel>().FirstOrDefault();
        if (toolbar is null)
            return;

        if (toolbar.Children
            .OfType<Button>()
            .Any(button => string.Equals(
                button.Content?.ToString(),
                "Export to Unity",
                StringComparison.Ordinal)))
        {
            _unityExportToolbarInstalled = true;
            return;
        }

        var button = new Button
        {
            Content = "Export to Unity",
            Background = new SolidColorBrush(Color.FromRgb(48, 56, 65)),
            Foreground = new SolidColorBrush(Color.FromRgb(241, 244, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 70, 81))
        };
        button.Click += ExportToUnity_Click;
        toolbar.Children.Add(button);
        _unityExportToolbarInstalled = true;
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
        var unityProjectPath = GetConfiguredUnityProjectPath();
        if (!_unityExport.IsUnityProject(unityProjectPath))
        {
            StatusText.Text = "Choose a Unity project before opening the generated scripts folder";
            return;
        }

        var generatedRoot = _unityExport.GetGeneratedRoot(unityProjectPath);
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

        var unityProjectPath = GetConfiguredUnityProjectPath();
        if (!_unityExport.IsUnityProject(unityProjectPath))
        {
            if (!ChooseUnityProject())
                return;

            unityProjectPath = GetConfiguredUnityProjectPath();
        }

        CaptureEditorStateForAutosave();
        CommitVariableEdits();

        var previousSaveState = SaveStateText.Text;
        _unityExportBusy = true;
        SaveStateText.Text = "Exporting...";
        StatusText.Text = "Assembling Code Loom files into normal Unity C# scripts...";
        _unityExportView?.ShowMessage("Exporting generated scripts...");

        try
        {
            var result = _unityExport.Export(_project, unityProjectPath);
            _unityExportView?.LoadResult(result);

            if (!result.Success)
            {
                StatusText.Text = "Unity export failed";
                MessageBox.Show(
                    this,
                    result.Message,
                    "Unity export failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

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
            SaveStateText.Text = previousSaveState;
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

        _settings.UnityProjectPaths ??= new Dictionary<string, string>();
        _settings.UnityProjectPaths[GetUnityTargetContextKey()] = normalized;
        _storage.SaveSettings(_settings);
        RefreshUnityExportUi();
        StatusText.Text = $"Unity target: {Path.GetFileName(normalized)}";
        return true;
    }

    private string GetConfiguredUnityProjectPath()
    {
        var targets = _settings.UnityProjectPaths;
        if (targets is null)
            return string.Empty;

        return targets.TryGetValue(GetUnityTargetContextKey(), out var path)
            ? path
            : string.Empty;
    }

    private string GetUnityTargetContextKey()
    {
        return HasRepository()
            ? "repo|" + Path.GetFullPath(_settings.GitRepositoryPath).ToUpperInvariant()
            : "local|" + _project.Name;
    }

    private void RefreshUnityExportUi()
    {
        var unityProjectPath = GetConfiguredUnityProjectPath();
        _unityExportView?.LoadTarget(
            unityProjectPath,
            _unityExport.IsUnityProject(unityProjectPath));
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
