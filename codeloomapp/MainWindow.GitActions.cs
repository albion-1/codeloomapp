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
            ToolTip = "Get incoming GitHub changes, then rescan physical repository C# files."
        };
        pullButton.Click += PullGitHub_Click;

        var pushButton = new Button
        {
            Content = "Push",
            ToolTip = "Save physical C# files and Code Loom metadata, commit them, then push.",
            Background = new SolidColorBrush(Color.FromRgb(48, 56, 65)),
            Foreground = new SolidColorBrush(Color.FromRgb(241, 244, 247)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 70, 81))
        };
        pushButton.Click += PushGitHub_Click;

        toolbar.Children.Insert(insertIndex, pullButton);
        toolbar.Children.Insert(insertIndex + 1, pushButton);

        _mainGitActionsUiInstalled = true;
    }

    private async void PullGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMainGitAction("pull"))
            return;

        try
        {
            SaveEditorToActiveSubfile();
            CommitVariableEdits();
            if (!TrySaveRepositoryProject(showConfirmation: false))
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

            // Physical .cs files are now the source of truth. Always rebuild the Code
            // Loom projection after a successful pull, regardless of project.json changes.
            var loaded = LoadRepositoryProjectFromDisk(
                showChangeSummary: true,
                status: "Pulled GitHub changes and refreshed physical C# files");

            SaveStateText.Text = "Pulled";
            StatusText.Text = loaded.Scan.Changes.Count > 0
                ? $"{result.Message} Refreshed {loaded.Scan.Changes.Count} C# change(s)."
                : result.Message;
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
            SaveEditorToActiveSubfile();
            CommitVariableEdits();
            if (!TrySaveRepositoryProject(showConfirmation: false))
                return;

            SaveStateText.Text = "Pushing...";
            StatusText.Text = "Committing physical C# files and Code Loom metadata, then pushing to GitHub...";

            var result = await _git.SyncAsync(_settings.GitRepositoryPath);
            if (!result.Success)
            {
                ShowGitActionFailure("GitHub push paused", GitActionFriendlyMessage(result.Message));
                return;
            }

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
        LoadRepositoryBacked_Click(sender, e);
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
            LoadRepositoryProjectFromDisk(
                showChangeSummary: false,
                status: historyLabel);
            CaptureImmediateHistory(historyLabel);
            _lastAutosavedFingerprint = _storage.SerializeProject(_project);
        }
        catch
        {
            // The Git action already succeeded. Keep the current in-memory project if
            // repository source cannot be reloaded for an unrelated local I/O reason.
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
