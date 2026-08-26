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
            ToolTip = "Read-only scan of repository .cs files. Does not import, edit, or overwrite source files."
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
                _repositoryCSharpSnapshot = null;
                _repositoryCSharpSnapshotRoot = repositoryRoot;
            }

            var result = _repositoryCSharpScanner.Scan(repositoryRoot, _repositoryCSharpSnapshot);
            _repositoryCSharpSnapshot = RepositoryCSharpScanner.CreateSnapshot(result);

            var added = result.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Added);
            var changed = result.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Changed);
            var removed = result.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Removed);

            StatusText.Text = result.IsFirstScan
                ? $"C# scan found {result.Files.Count} source file(s). No files were modified."
                : $"C# scan: {added} new, {changed} changed, {removed} removed. No files were modified.";

            MessageBox.Show(
                this,
                BuildRepositoryScanSummary(result),
                "C# repository scan",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

    private static string BuildRepositoryScanSummary(RepositoryCSharpScanResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"C# files found: {result.Files.Count}");
        builder.AppendLine("Only .cs files were inspected. Source files were not changed.");
        builder.AppendLine("Unity/build/package/generated folders are excluded.");
        builder.AppendLine();

        if (result.Changes.Count == 0)
        {
            builder.Append("No changes since the previous scan in this session.");
            return builder.ToString();
        }

        if (result.IsFirstScan)
            builder.AppendLine("First scan: existing source files are listed as discovered/new.");
        else
            builder.AppendLine("Changes since the previous scan:");

        builder.AppendLine();

        const int displayLimit = 24;
        foreach (var change in result.Changes.Take(displayLimit))
        {
            var label = change.Kind switch
            {
                RepositoryCSharpChangeKind.Added => "NEW",
                RepositoryCSharpChangeKind.Changed => "CHANGED",
                RepositoryCSharpChangeKind.Removed => "REMOVED",
                _ => "CHANGE"
            };

            builder.AppendLine($"{label}  {change.RelativePath}");
        }

        if (result.Changes.Count > displayLimit)
            builder.AppendLine($"...and {result.Changes.Count - displayLimit} more.");

        builder.AppendLine();
        builder.Append("This scanner is detection-only for now; it does not import changes into Code Loom.");
        return builder.ToString();
    }
}
