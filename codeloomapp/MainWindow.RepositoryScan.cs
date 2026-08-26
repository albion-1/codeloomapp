using System.Windows;
using System.Windows.Controls;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly RepositoryCSharpScanner _repositoryCSharpScanner = new();
    private Dictionary<string, string>? _repositoryCSharpSnapshot;
    private string _repositoryCSharpSnapshotRoot = string.Empty;
    private bool _repositoryScannerUiInstalled;

    private void EnsureRepositoryScannerUi()
    {
        EnsureRepositoryBackedProjectUi();

        if (_repositoryScannerUiInstalled || Content is not Grid root)
            return;

        var topRow = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 0);
        var toolbar = topRow?.Children.OfType<WrapPanel>().FirstOrDefault();
        if (toolbar is null)
            return;

        var scanButton = new Button
        {
            Content = "Scan C#",
            ToolTip = "Scan physical repository .cs files and refresh Code Loom when you approve the detected changes."
        };
        scanButton.Click += ScanRepositoryCSharp_Click;

        var chooseButton = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Choose Existing Repo", StringComparison.Ordinal));

        var insertIndex = chooseButton is null
            ? toolbar.Children.Count
            : toolbar.Children.IndexOf(chooseButton) + 1;

        toolbar.Children.Insert(insertIndex, scanButton);
        _repositoryScannerUiInstalled = true;
    }

    private void ScanRepositoryCSharp_Click(object sender, RoutedEventArgs e)
    {
        if (!HasRepository())
        {
            MessageBox.Show(
                this,
                "Choose a Git repository before scanning for C# files.",
                "C# repository scan",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveEditorToActiveSubfile();
            CommitVariableEdits();

            var repositoryRoot = Path.GetFullPath(_settings.GitRepositoryPath);
            if (!string.Equals(_repositoryCSharpSnapshotRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                _repositoryCSharpSnapshot = _repositoryProject.CreateProjectSnapshot(_project);
                _repositoryCSharpSnapshotRoot = repositoryRoot;
            }

            var result = _repositoryCSharpScanner.Scan(repositoryRoot, _repositoryCSharpSnapshot);
            if (result.Changes.Count == 0)
            {
                StatusText.Text = $"C# scan: {result.Files.Count} physical source file(s), no changes";
                MessageBox.Show(
                    this,
                    "No physical C# files changed since Code Loom last loaded this repository.",
                    "C# repository scan",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var summary = BuildRepositoryChangeSummary(result, Array.Empty<string>());
            var answer = MessageBox.Show(
                this,
                summary + "\n\nRefresh Code Loom from these physical .cs files now?\n\nExternal source is never silently overwritten.",
                "C# repository changes found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "C# changes detected · refresh postponed";
                return;
            }

            var localEdits = _repositoryProject.GetLocallyModifiedSourcePaths(_project);
            if (localEdits.Count > 0)
            {
                var externallyChanged = result.Changes
                    .Where(change => change.Kind is RepositoryCSharpChangeKind.Changed or RepositoryCSharpChangeKind.Removed)
                    .Select(change => change.RelativePath)
                    .Concat(result.Changes
                        .Where(change => change.Kind == RepositoryCSharpChangeKind.Renamed)
                        .Select(change => change.PreviousRelativePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var overlap = localEdits.Where(externallyChanged.Contains).ToList();

                if (overlap.Count > 0)
                {
                    MessageBox.Show(
                        this,
                        "The same C# file changed both inside Code Loom and on disk. Code Loom will not choose one version automatically.\n\n" +
                        string.Join("\n", overlap.Take(8).Select(path => "• " + path)) +
                        "\n\nResolve or save that file intentionally before refreshing.",
                        "C# edit conflict",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    StatusText.Text = "C# refresh paused · overlapping edits need review";
                    return;
                }

                if (!TrySaveRepositoryProject(showConfirmation: false))
                    return;
            }

            LoadRepositoryProjectFromDisk(
                showChangeSummary: true,
                status: "Refreshed Code Loom from physical repository C# files");
        }
        catch (Exception exception)
        {
            StatusText.Text = "C# repository scan failed";
            MessageBox.Show(
                this,
                "Code Loom could not scan this repository.\n\n" + exception.Message,
                "C# repository scan",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
