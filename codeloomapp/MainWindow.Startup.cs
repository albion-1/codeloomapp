using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _startupStateFinalized;

    private void FinalizeStartupProjectStateOnce()
    {
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
            _settings.GitRepositoryPath = string.Empty;
            _storage.SaveSettings(_settings);
            RefreshRepositoryDisplay();
            SetBlankProjectState("Previous project was not found");
            return;
        }

        try
        {
            var loaded = LoadRepositoryProjectFromDisk(
                showChangeSummary: false,
                status: "Reopened repository C# project");

            var physicalCount = loaded.Scan.Files.Count;
            if (physicalCount == 0)
                StatusText.Text = "Reopened repository · no eligible physical C# files found";
            else if (loaded.MigratedLegacyProject)
                StatusText.Text = $"Reopened {physicalCount} physical C# file(s) · legacy Code Loom metadata migration ready";
            else
                StatusText.Text = $"Reopened {physicalCount} physical C# file(s)";
        }
        catch (Exception exception)
        {
            SetBlankProjectState("Repository C# project could not be loaded");
            MessageBox.Show(
                this,
                "Code Loom remembered the repository, but could not rebuild the project from its physical C# files.\n\n" + exception.Message,
                "Startup project load",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
