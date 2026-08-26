using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _startupStateFinalized;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (_startupStateFinalized)
            return;

        _startupStateFinalized = true;
        FinalizeStartupProjectState();
        HideLegacyDemoControl();
    }

    private void FinalizeStartupProjectState()
    {
        var rememberedPath = _settings.GitRepositoryPath;

        if (string.IsNullOrWhiteSpace(rememberedPath))
        {
            SetBlankProjectState("No project selected");
            return;
        }

        if (!HasRepository())
        {
            // Do not keep retrying a repository that has been moved, deleted, or is no
            // longer a Git repository. The user can select it again if it comes back.
            _settings.GitRepositoryPath = string.Empty;
            _storage.SaveSettings(_settings);
            RefreshRepositoryDisplay();
            SetBlankProjectState("Previous project was not found");
            return;
        }

        try
        {
            var rememberedProject = _storage.LoadProject(rememberedPath);
            if (rememberedProject is null)
            {
                // A valid repository may not have been initialized as a Code Loom project
                // yet. Keep the repository selected, but never inject sample gameplay code.
                SetBlankProjectState("Repository selected — no Code Loom project yet");
                return;
            }

            // The constructor already loads remembered projects. Re-apply the loaded model
            // here so startup has one deterministic final state even after upgrading from
            // versions that created the built-in demo as a fallback.
            _project = rememberedProject;
            RefreshEntireProjectUi();
            RefreshProjectTree();
            SaveStateText.Text = "Loaded from disk";
            StatusText.Text = $"Reopened {_project.Name}";
        }
        catch
        {
            // Corrupt/unreadable project metadata should not manufacture a demo project.
            SetBlankProjectState("Code Loom project could not be loaded");
        }
    }

    private void SetBlankProjectState(string status)
    {
        _project = new CodeProject { Name = "Untitled Project" };
        _activeFile = null;
        _activeSubfile = null;

        RefreshEntireProjectUi();
        RefreshProjectTree();

        SubfileList.ItemsSource = null;
        VariablesGrid.ItemsSource = null;
        FlowItems.ItemsSource = null;
        ActiveFileText.Text = "No file selected";
        AssembledFileName.Text = string.Empty;
        AssembledCodeBox.Text = string.Empty;
        SaveStateText.Text = "No project loaded";
        StatusText.Text = status;
    }

    private void HideLegacyDemoControl()
    {
        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (string.Equals(button.Content?.ToString(), "Reset Demo", StringComparison.Ordinal))
                button.Visibility = Visibility.Collapsed;
        }
    }
}
