using System.Text;
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
                summary + "\n\nRefresh Code Loom from these physical .cs files now?\n\nCode Loom will preserve external edits and will not overwrite a file that was changed both inside and outside the app.",
                "C# repository changes found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "C# changes detected · refresh postponed";
                return;
            }

            // Save independent Code Loom edits first. The repository writer only blocks
            // when the same physical file was edited on both sides.
            SaveEditorToActiveSubfile();
            CommitVariableEdits();
            if (!TrySaveRepositoryProject(showConfirmation: false))
                return;

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
