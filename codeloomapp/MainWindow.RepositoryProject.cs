using System.Text;
using System.Windows;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly RepositoryProjectService _repositoryProject = new();

    private RepositoryProjectLoadResult LoadRepositoryProjectFromDisk(
        bool showChangeSummary,
        string status)
    {
        if (!HasRepository())
            throw new InvalidOperationException("No Git repository is selected.");

        var stored = _storage.LoadRepositoryMetadata(_settings.GitRepositoryPath);
        var loaded = _repositoryProject.Load(_settings.GitRepositoryPath, stored);

        _project = loaded.Project;
        _activeFile = null;
        _activeSubfile = null;
        _repositoryCSharpSnapshot = RepositoryCSharpScanner.CreateSnapshot(loaded.Scan);
        _repositoryCSharpSnapshotRoot = Path.GetFullPath(_settings.GitRepositoryPath);

        RefreshEntireProjectUi();
        RefreshProjectTree();
        SaveStateText.Text = "Loaded from repository";
        StatusText.Text = status;
        _lastAutosavedFingerprint = _storage.SerializeProject(_project);

        if (showChangeSummary && loaded.Scan.Changes.Count > 0)
        {
            MessageBox.Show(
                this,
                BuildRepositoryChangeSummary(loaded.Scan, loaded.Warnings),
                "Repository C# changes",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else if (loaded.Warnings.Count > 0)
        {
            StatusText.Text = $"{status} · {loaded.Warnings.Count} C# import note(s)";
        }

        return loaded;
    }

    private bool TrySaveRepositoryProject(bool showConfirmation, bool showErrors = true)
    {
        if (!HasRepository() && !SelectRepository())
            return false;

        try
        {
            var result = _repositoryProject.Save(_project, _settings.GitRepositoryPath, _storage);
            if (!result.Success)
            {
                SaveStateText.Text = "Source conflict";
                StatusText.Text = "External C# changes were preserved; Code Loom did not overwrite them.";

                if (showErrors)
                {
                    var details = string.Join(
                        "\n\n",
                        result.Conflicts.Take(8).Select(conflict =>
                            $"{conflict.RelativePath}\n{conflict.Reason}"));
                    MessageBox.Show(
                        this,
                        "Code Loom stopped before writing because one or more physical C# files changed outside the app.\n\n" + details,
                        "C# source changed outside Code Loom",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return false;
            }

            _repositoryCSharpSnapshot = _repositoryProject.CreateProjectSnapshot(_project);
            _repositoryCSharpSnapshotRoot = Path.GetFullPath(_settings.GitRepositoryPath);
            _lastAutosavedFingerprint = _storage.SerializeProject(_project);
            SaveStateText.Text = "Saved";
            StatusText.Text = result.WrittenCount == 0
                ? "C# source and Code Loom metadata are up to date"
                : result.WrittenCount == 1
                    ? "Saved 1 physical C# file and Code Loom metadata"
                    : $"Saved {result.WrittenCount} physical C# files and Code Loom metadata";

            if (showConfirmation)
            {
                var warning = result.Warnings.Count > 0
                    ? $"\n\n{result.Warnings.Count} legacy/unmapped note(s) were preserved."
                    : string.Empty;
                MessageBox.Show(
                    this,
                    $"Saved ordinary repository .cs files plus metadata at:\n{_storage.GetProjectFilePath(_settings.GitRepositoryPath)}{warning}",
                    "Code Loom",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }
        catch (Exception exception)
        {
            SaveStateText.Text = "Save failed";
            StatusText.Text = "Code Loom could not save repository source";
            if (showErrors)
            {
                MessageBox.Show(
                    this,
                    "Code Loom could not save the repository project.\n\n" + exception.Message,
                    "Save failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            return false;
        }
    }

    private static string BuildRepositoryChangeSummary(
        RepositoryCSharpScanResult scan,
        IReadOnlyList<string> warnings)
    {
        var builder = new StringBuilder();
        var added = scan.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Added);
        var changed = scan.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Changed);
        var removed = scan.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Removed);
        var renamed = scan.Changes.Count(change => change.Kind == RepositoryCSharpChangeKind.Renamed);

        builder.AppendLine($"Repository C# files: {scan.Files.Count}");
        builder.AppendLine($"New: {added} · Changed: {changed} · Removed: {removed} · Renamed: {renamed}");
        builder.AppendLine();

        foreach (var change in scan.Changes.Take(24))
        {
            var line = change.Kind switch
            {
                RepositoryCSharpChangeKind.Added => $"NEW  {change.RelativePath}",
                RepositoryCSharpChangeKind.Changed => $"CHANGED  {change.RelativePath}",
                RepositoryCSharpChangeKind.Removed => $"REMOVED  {change.RelativePath}",
                RepositoryCSharpChangeKind.Renamed => $"RENAMED  {change.PreviousRelativePath} → {change.RelativePath}",
                _ => change.RelativePath
            };
            builder.AppendLine(line);
        }

        if (scan.Changes.Count > 24)
            builder.AppendLine($"…and {scan.Changes.Count - 24} more.");

        if (warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Import notes:");
            foreach (var warning in warnings.Take(6))
                builder.AppendLine("• " + warning);
            if (warnings.Count > 6)
                builder.AppendLine($"• …and {warnings.Count - 6} more.");
        }

        return builder.ToString().TrimEnd();
    }
}
