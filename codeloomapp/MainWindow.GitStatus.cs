using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using codeloomapp.Services;
using codeloomapp.Views;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly DispatcherTimer _gitStatusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(6)
    };

    private GitStatusView? _gitStatusView;
    private bool _gitStatusUiInstalled;
    private bool _gitStatusRefreshBusy;
    private DateTime _lastRemoteFetchUtc = DateTime.MinValue;

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        EnsureGitStatusUi();
        var shouldCheckRemote = DateTime.UtcNow - _lastRemoteFetchUtc > TimeSpan.FromSeconds(45);
        _ = RefreshGitStatusAsync(fetchRemote: shouldCheckRemote);
    }

    private void EnsureGitStatusUi()
    {
        if (_gitStatusUiInstalled)
            return;

        if (GitHubAccountText.Parent is not Grid existingStatusGrid
            || existingStatusGrid.Parent is not Border statusBorder)
        {
            return;
        }

        _gitStatusUiInstalled = true;
        _gitStatusView = new GitStatusView();
        _gitStatusView.RefreshRequested += GitStatusView_RefreshRequested;
        _gitStatusView.ContinueRebaseRequested += GitStatusView_ContinueRebaseRequested;
        _gitStatusView.ApplyRemoteUpdatesRequested += GitStatusView_ApplyRemoteUpdatesRequested;

        statusBorder.Child = null;
        var stack = new StackPanel();
        stack.Children.Add(existingStatusGrid);
        stack.Children.Add(_gitStatusView);
        statusBorder.Child = stack;

        _gitStatusTimer.Tick += GitStatusTimer_Tick;
        _gitStatusTimer.Start();

        Closed += (_, _) =>
        {
            _gitStatusTimer.Stop();
            _gitStatusTimer.Tick -= GitStatusTimer_Tick;
        };
    }

    private async void GitStatusTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsActive)
            return;

        var shouldCheckRemote = DateTime.UtcNow - _lastRemoteFetchUtc > TimeSpan.FromSeconds(45);
        await RefreshGitStatusAsync(fetchRemote: shouldCheckRemote);
    }

    private async void GitStatusView_RefreshRequested(object? sender, EventArgs e)
    {
        await RefreshGitStatusAsync(fetchRemote: true);
    }

    private async void GitStatusView_ContinueRebaseRequested(object? sender, EventArgs e)
    {
        if (!HasRepository() || _gitStatusRefreshBusy)
            return;

        _gitStatusRefreshBusy = true;
        SaveStateText.Text = "Git operation...";

        try
        {
            var result = await _git.ContinueRebaseAsync(_settings.GitRepositoryPath);
            StatusText.Text = result.Message;

            if (!result.Success)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    "Git rebase",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                TryReloadProjectAfterGitChange();
            }
        }
        finally
        {
            _gitStatusRefreshBusy = false;
            await RefreshGitStatusAsync(fetchRemote: false);
        }
    }

    private async void GitStatusView_ApplyRemoteUpdatesRequested(object? sender, EventArgs e)
    {
        if (!HasRepository() || _gitStatusRefreshBusy)
            return;

        // Flush any editor state before asking Git whether the tree is clean. If the
        // user has local Code Loom work, that work becomes a visible .codeloom change
        // and the fast-forward workflow will refuse to overwrite it.
        CaptureEditorStateForAutosave();
        try
        {
            _storage.SaveProject(_project, _settings.GitRepositoryPath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "Code Loom could not save your current project before applying remote changes.\n\n" + exception.Message,
                "Remote updates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _gitStatusRefreshBusy = true;
        SaveStateText.Text = "Applying remote updates...";
        StatusText.Text = "Bringing reviewed GitHub changes into the local repository...";

        try
        {
            var preview = await _git.GetRemoteChangePreviewAsync(_settings.GitRepositoryPath, fetchRemote: true);
            _lastRemoteFetchUtc = DateTime.UtcNow;

            if (!preview.HasIncomingChanges)
            {
                StatusText.Text = preview.Message;
                SaveStateText.Text = "Saved";
                return;
            }

            var result = await _git.ApplyRemoteChangesAsync(_settings.GitRepositoryPath);
            StatusText.Text = result.Message;

            if (!result.Success)
            {
                SaveStateText.Text = "Remote update paused";
                MessageBox.Show(
                    this,
                    result.Message,
                    "Remote updates",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (preview.ProjectDataChanged)
            {
                TryReloadProjectAfterGitChange();
                StatusText.Text = result.Message + " Code Loom project data was reloaded.";
            }
            else
            {
                SaveStateText.Text = "Remote updated";
                StatusText.Text = result.Message + " Repository files are now updated on disk.";
            }
        }
        finally
        {
            _gitStatusRefreshBusy = false;
            await RefreshGitStatusAsync(fetchRemote: false);
        }
    }

    private async Task RefreshGitStatusAsync(bool fetchRemote)
    {
        if (_gitStatusView is null || _gitStatusRefreshBusy)
            return;

        if (!HasRepository())
        {
            _gitStatusView.LoadUnavailable("Choose or create a Git repository to see branch and sync status.");
            return;
        }

        _gitStatusRefreshBusy = true;
        if (fetchRemote)
            _gitStatusView.SetChecking();

        try
        {
            var status = await _git.GetStatusAsync(_settings.GitRepositoryPath, fetchRemote);
            if (fetchRemote)
                _lastRemoteFetchUtc = DateTime.UtcNow;

            if (!status.Available)
            {
                _gitStatusView.LoadUnavailable(status.WarningMessage);
                return;
            }

            _gitStatusView.LoadStatus(status);

            var preview = await _git.GetRemoteChangePreviewAsync(
                _settings.GitRepositoryPath,
                fetchRemote: false);
            _gitStatusView.LoadRemotePreview(preview, status);
        }
        finally
        {
            _gitStatusRefreshBusy = false;
        }
    }

    private void TryReloadProjectAfterGitChange()
    {
        try
        {
            var project = _storage.LoadProject(_settings.GitRepositoryPath);
            if (project is null)
                return;

            _project = project;
            RefreshEntireProjectUi();
            RefreshProjectTree();
            SaveStateText.Text = "Git updated";
        }
        catch
        {
            // Git may have completed even if the project refresh fails. Keep the
            // current in-memory project rather than replacing it with partial data.
        }
    }
}
