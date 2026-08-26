using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly DispatcherTimer _autosaveTimer = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    private string _lastAutosavedFingerprint = string.Empty;
    private bool _autosaveBusy;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        FinalizeStartupProjectStateOnce();
        TryRestoreRecoverySnapshot();
        InstallAutosaveUiHooks();
        InitializeProjectHistory();

        _lastAutosavedFingerprint = _storage.SerializeProject(_project);
        _autosaveTimer.Tick += AutosaveTimer_Tick;
        _autosaveTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= AutosaveTimer_Tick;

        try
        {
            CaptureEditorStateForAutosave();
            var currentFingerprint = _storage.SerializeProject(_project);

            if (HasRepository())
            {
                var diskProject = _storage.LoadProject(_settings.GitRepositoryPath);
                var diskFingerprint = diskProject is null
                    ? string.Empty
                    : _storage.SerializeProject(diskProject);

                if (string.Equals(currentFingerprint, diskFingerprint, StringComparison.Ordinal))
                {
                    _storage.DeleteRecoverySnapshot();
                }
                else
                {
                    SaveRecoverySnapshot(cleanShutdown: false);
                }
            }
            else
            {
                SaveRecoverySnapshot(cleanShutdown: true);
            }
        }
        catch
        {
        }

        base.OnClosed(e);
    }

    private void InstallAutosaveUiHooks()
    {
        CodeBox.TextChanged += AutosaveEditorChanged;
        SubfileNameBox.TextChanged += AutosaveEditorChanged;
        RoleBox.TextChanged += AutosaveEditorChanged;
        ReceivesBox.TextChanged += AutosaveEditorChanged;
        ReturnsBox.TextChanged += AutosaveEditorChanged;
        UsedByBox.TextChanged += AutosaveEditorChanged;
        PurposeBox.TextChanged += AutosaveEditorChanged;
    }

    private void AutosaveEditorChanged(object? sender, EventArgs e)
    {
        if (_isLoadingEditor || _autosaveBusy)
            return;

        SaveStateText.Text = "Unsaved";
    }

    private void AutosaveTimer_Tick(object? sender, EventArgs e)
    {
        if (_autosaveBusy)
            return;

        CaptureEditorStateForAutosave();
        var fingerprint = _storage.SerializeProject(_project);

        if (string.Equals(fingerprint, _lastAutosavedFingerprint, StringComparison.Ordinal))
            return;

        CaptureProjectHistory(fingerprint, DescribeHistoryChange());

        _autosaveBusy = true;
        SaveStateText.Text = "Saving...";

        try
        {
            SaveRecoverySnapshot(cleanShutdown: false);

            if (HasRepository())
            {
                _storage.SaveProject(_project, _settings.GitRepositoryPath);
                SaveStateText.Text = "Saved";
                StatusText.Text = $"Autosaved at {DateTime.Now:t}";
            }
            else
            {
                SaveStateText.Text = "Saved locally";
                StatusText.Text = "Autosaved a local recovery copy · choose a Git repository for project storage";
            }

            _lastAutosavedFingerprint = fingerprint;
        }
        catch
        {
            SaveStateText.Text = "Autosave failed";
            StatusText.Text = "Code Loom could not write the autosave recovery copy";
        }
        finally
        {
            _autosaveBusy = false;
        }
    }

    private void CaptureEditorStateForAutosave()
    {
        if (_isLoadingEditor || _activeSubfile is null)
            return;

        if (!string.IsNullOrWhiteSpace(SubfileNameBox.Text))
            _activeSubfile.Name = SubfileNameBox.Text.Trim();

        _activeSubfile.Role = RoleBox.Text.Trim();
        _activeSubfile.Code = CodeBox.Text;
        _activeSubfile.Receives = ReceivesBox.Text.Trim();
        _activeSubfile.Returns = ReturnsBox.Text.Trim();
        _activeSubfile.UsedBy = UsedByBox.Text.Trim();
        _activeSubfile.Purpose = PurposeBox.Text.Trim();
    }

    private void SaveRecoverySnapshot(bool cleanShutdown)
    {
        _storage.SaveRecoverySnapshot(new RecoverySnapshot
        {
            SavedAtUtc = DateTime.UtcNow,
            RepositoryPath = HasRepository() ? _settings.GitRepositoryPath : string.Empty,
            CleanShutdown = cleanShutdown,
            Project = _project
        });
    }

    private void TryRestoreRecoverySnapshot()
    {
        var snapshot = _storage.LoadRecoverySnapshot();
        if (snapshot is null || snapshot.Project is null)
            return;

        var sameRepository = HasRepository()
                             && string.Equals(
                                 snapshot.RepositoryPath,
                                 _settings.GitRepositoryPath,
                                 StringComparison.OrdinalIgnoreCase);

        if (sameRepository)
        {
            var projectWriteTime = _storage.GetProjectLastWriteTimeUtc(_settings.GitRepositoryPath);
            if (projectWriteTime.HasValue
                && projectWriteTime.Value >= snapshot.SavedAtUtc.AddSeconds(-1))
            {
                _storage.DeleteRecoverySnapshot();
                return;
            }
        }

        if (snapshot.CleanShutdown && string.IsNullOrWhiteSpace(snapshot.RepositoryPath))
        {
            RestoreSnapshot(snapshot, "Restored local autosave from the previous session");
            return;
        }

        var source = string.IsNullOrWhiteSpace(snapshot.RepositoryPath)
            ? "a local Code Loom session"
            : snapshot.RepositoryPath;

        var answer = MessageBox.Show(
            this,
            $"Code Loom found a recovery copy newer than the last normal project save.\n\nProject: {snapshot.Project.Name}\nFrom: {source}\nAutosaved: {snapshot.SavedAtUtc.ToLocalTime():g}\n\nRecover these changes?",
            "Recover autosaved work",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            RestoreSnapshot(snapshot, "Recovered autosaved work");
            return;
        }

        _storage.DeleteRecoverySnapshot();
    }

    private void RestoreSnapshot(RecoverySnapshot snapshot, string status)
    {
        _project = snapshot.Project;

        if (!HasRepository()
            && !string.IsNullOrWhiteSpace(snapshot.RepositoryPath)
            && Directory.Exists(snapshot.RepositoryPath)
            && _git.IsGitRepository(snapshot.RepositoryPath))
        {
            _settings.GitRepositoryPath = snapshot.RepositoryPath;
            _storage.SaveSettings(_settings);
            RefreshRepositoryDisplay();
        }

        RefreshEntireProjectUi();
        RefreshProjectTree();
        SaveStateText.Text = "Recovered";
        StatusText.Text = status;
    }
}
