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

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        EnsureGitStatusUi();
        _ = RefreshGitStatusAsync(fetchRemote: false);
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

        await RefreshGitStatusAsync(fetchRemote: false);
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
            if (!status.Available)
            {
                _gitStatusView.LoadUnavailable(status.WarningMessage);
                return;
            }

            _gitStatusView.LoadStatus(status);
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
