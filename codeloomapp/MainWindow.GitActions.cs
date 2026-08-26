using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _mainGitActionsUiInstalled;
    private bool _mainGitActionBusy;

    private void EnsureMainGitActionsUi()
    {
        if (_mainGitActionsUiInstalled || Content is not Grid root)
            return;

        var topRow = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 0);
        var toolbar = topRow?.Children.OfType<WrapPanel>().FirstOrDefault();
        if (toolbar is null)
            return;

        var syncButton = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Sync", StringComparison.Ordinal));

        var insertIndex = syncButton is null
            ? toolbar.Children.Count
            : toolbar.Children.IndexOf(syncButton);

        if (syncButton is not null)
            toolbar.Children.Remove(syncButton);

        var pullButton = new Button
        {
            Content = "Pull",
            ToolTip = "Get incoming GitHub changes. Code Loom will not overwrite local unpushed work."
        };
        pullButton.Click += PullGitHub_Click;

        var pushButton = new Button
        {
            Content = "Push",
            ToolTip = "Save Code Loom changes, safely incorporate GitHub updates if needed, then push.",
            Background = new SolidColorBrush(Color.FromRgb(48, 56, 65)),
            Foreground = new SolidColorBrush(Color.FromRgb(241, 244, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 70, 81))
        };
        pushButton.Click += PushGitHub_Click;

        toolbar.Children.Insert(insertIndex, pullButton);
        toolbar.Children.Insert(insertIndex + 1, pushButton);

        // Manual Load is still useful, but make it explicit when it would replace
        // edits that have not reached the on-disk project yet.
        var loadButton = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Load", StringComparison.Ordinal));
        if (loadButton is not null)
        {
            loadButton.Click -= LoadProject_Click;
            loadButton.Click += SafeLoadProject_Click;
            loadButton.ToolTip = "Reload this Code Loom project from its local repository.";
        }

        // Reset Demo was useful while building the app, but one accidental click can
        // replace a real project and then be autosaved. Do not expose it in releases.
        var bottomRow = root.Children
            .OfType<DockPanel>()
            .FirstOrDefault(panel => Grid.GetRow(panel) == 3);
        var resetButton = bottomRow?.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Reset Demo", StringComparison.Ordinal));
        if (resetButton is not null)
            resetButton.Visibility = Visibility.Collapsed;

        _mainGitActionsUiInstalled = true;
    }

    private async void PullGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMainGitAction("pull"))
            return;

        try
        {
            if (!SaveProjectToDisk(false))
                return;

            SaveStateText.Text = "Pulling...";
            StatusText.Text = "Checking GitHub for incoming changes...";

            var preview = await _git.GetRemoteChangePreviewAsync(
                _settings.GitRepositoryPath,
                fetchRemote: true);
            _lastRemoteFetchUtc = DateTime.UtcNow;

            if (!preview.Available)
            {
                ShowGitActionFailure("GitHub pull failed", GitActionFriendlyMessage(preview.Message));
                return;
            }

            if (!preview.HasIncomingChanges)
            {
                SaveStateText.Text = preview.Ahead > 0 ? "Local commits need Push" : "Up to date";
                StatusText.Text = preview.Ahead > 0
                    ? "Local commits are waiting to go to GitHub — use Push."
                    : GitActionFriendlyMessage(preview.Message);
                return;
            }

            var result = await _git.ApplyRemoteChangesAsync(_settings.GitRepositoryPath);
            if (!result.Success)
            {
                ShowGitActionFailure("GitHub pull paused", GitActionFriendlyMessage(result.Message));
                return;
            }

            if (preview.ProjectDataChanged)
                ReloadProjectAfterMainGitAction("Pulled GitHub changes");

            SaveStateText.Text = "Pulled";
            var message = GitActionFriendlyMessage(result.Message);
            StatusText.Text = preview.ProjectDataChanged
                ? message + " Code Loom project data was reloaded."
                : message;
        }
        finally
        {
            EndMainGitAction();
        }
    }

    private async void PushGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMainGitAction("push"))
            return;

        try
        {
            if (!SaveProjectToDisk(false))
                return;

            SaveStateText.Text = "Pushing...";
            StatusText.Text = "Saving, committing, and pushing Code Loom changes to GitHub...";

            var result = await _git.SyncAsync(_settings.GitRepositoryPath);
            if (!result.Success)
            {
                ShowGitActionFailure("GitHub push paused", GitActionFriendlyMessage(result.Message));
                return;
            }

            // Push can safely rebase incoming commits before sending local work. Reload
            // project.json in case that reconciliation changed the Code Loom project.
            ReloadProjectAfterMainGitAction("Pushed GitHub changes");
            SaveStateText.Text = "Pushed";
            StatusText.Text = GitActionFriendlyMessage(result.Message);
        }
        finally
        {
            EndMainGitAction();
        }
    }

    private void SafeLoadProject_Click(object sender, RoutedEventArgs e)
    {
        CaptureEditorStateForAutosave();

        if (HasRepository())
        {
            try
            {
                var diskProject = _storage.LoadProject(_settings.GitRepositoryPath);
                if (diskProject is not null)
                {
                    var currentJson = _storage.SerializeProject(_project);
                    var diskJson = _storage.SerializeProject(diskProject);
                    if (!string.Equals(currentJson, diskJson, StringComparison.Ordinal))
                    {
                        var answer = MessageBox.Show(
                            this,
                            "Reload the project from disk?\n\nThe current editor contains changes that are not in the on-disk project. Reloading will replace those changes.",
                            "Reload project",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (answer != MessageBoxResult.Yes)
                            return;
                    }
                }
            }
            catch
            {
                // The existing Load handler will show the useful read/load error.
            }
        }

        LoadProject_Click(sender, e);
        RefreshProjectTree();
    }

    private bool TryBeginMainGitAction(string action)
    {
        if (_mainGitActionBusy || _gitStatusRefreshBusy)
        {
            StatusText.Text = $"Git is already checking status or running another operation. Try {action} again in a moment.";
            return false;
        }

        _mainGitActionBusy = true;
        _gitStatusRefreshBusy = true;
        return true;
    }

    private void EndMainGitAction()
    {
        _mainGitActionBusy = false;
        _gitStatusRefreshBusy = false;
        _ = RefreshGitStatusAsync(fetchRemote: false);
    }

    private void ReloadProjectAfterMainGitAction(string historyLabel)
    {
        try
        {
            var project = _storage.LoadProject(_settings.GitRepositoryPath);
            if (project is null)
                return;

            _project = project;
            RefreshEntireProjectUi();
            RefreshProjectTree();
            CaptureImmediateHistory(historyLabel);
            _lastAutosavedFingerprint = _storage.SerializeProject(_project);
        }
        catch
        {
            // The Git action already succeeded. Keep the current in-memory project if
            // project.json cannot be reloaded for an unrelated local I/O reason.
        }
    }

    private void ShowGitActionFailure(string title, string message)
    {
        SaveStateText.Text = "Git action paused";
        StatusText.Text = message;
        MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static string GitActionFriendlyMessage(string message)
    {
        return (message ?? string.Empty)
            .Replace("syncing", "pushing", StringComparison.OrdinalIgnoreCase)
            .Replace("sync", "push", StringComparison.OrdinalIgnoreCase);
    }
}
