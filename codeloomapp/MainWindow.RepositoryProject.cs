using System.Text;
using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly RepositoryProjectService _repositoryProject = new();
    private bool _repositoryBackedProjectUiInstalled;

    private void EnsureRepositoryBackedProjectUi()
    {
        if (_repositoryBackedProjectUiInstalled || Content is not Grid root)
            return;

        var topRow = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 0);
        var toolbar = topRow?.Children.OfType<WrapPanel>().FirstOrDefault();
        if (toolbar is null)
            return;

        RewireToolbarButton(toolbar, "Choose Existing Repo", ChooseRepositoryBacked_Click);
        RewireToolbarButton(toolbar, "Save", SaveRepositoryBacked_Click);
        RewireToolbarButton(toolbar, "Load", LoadRepositoryBacked_Click);

        FileList.SelectionChanged += (_, _) =>
        {
            if (_activeFile is null)
                return;

            EnsureNewFileHasRepositoryPath(_activeFile);
            ActiveFileText.Text = !string.IsNullOrWhiteSpace(_activeFile.RepositoryRelativePath)
                ? _activeFile.RepositoryRelativePath
                : _activeFile.IsLegacyUnmapped
                    ? _activeFile.Name + " · legacy/unmapped"
                    : _activeFile.Name;
        };

        Closing += (_, _) =>
        {
            if (!HasRepository())
                return;

            SaveEditorToActiveSubfile();
            CommitVariableEdits();
            _ = TrySaveRepositoryProject(showConfirmation: false, showErrors: false);
        };

        _repositoryBackedProjectUiInstalled = true;
    }

    private void EnsureNewFileHasRepositoryPath(CodeFile file)
    {
        if (!HasRepository()
            || file.IsLegacyUnmapped
            || !string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
        {
            return;
        }

        var folder = FindFolderContaining(file);
        var directory = folder?.RepositoryRelativePath;
        if (string.IsNullOrWhiteSpace(directory)
            && folder is not null
            && !string.Equals(folder.Name, "(repository root)", StringComparison.OrdinalIgnoreCase)
            && !folder.Name.StartsWith("Legacy (unmapped)", StringComparison.OrdinalIgnoreCase))
        {
            directory = folder.Name;
        }

        file.RepositoryRelativePath = RepositoryProjectService.NormalizeRelativePath(
            string.IsNullOrWhiteSpace(directory)
                ? file.Name
                : Path.Combine(directory, file.Name));
        file.SourceHash = string.Empty;
        file.IsRepositoryBacked = true;
    }

    private void RewireToolbarButton(WrapPanel toolbar, string content, RoutedEventHandler handler)
    {
        var button = toolbar.Children
            .OfType<Button>()
            .FirstOrDefault(candidate => string.Equals(candidate.Content?.ToString(), content, StringComparison.Ordinal));
        if (button is null)
            return;

        if (content == "Choose Existing Repo")
            button.Click -= ChooseRepository_Click;
        else if (content == "Save")
            button.Click -= SaveProject_Click;
        else if (content == "Load")
            button.Click -= LoadProject_Click;

        button.Click += handler;
    }

    private void ChooseRepositoryBacked_Click(object sender, RoutedEventArgs e)
    {
        if (!SelectRepository())
            return;

        try
        {
            var loaded = LoadRepositoryProjectFromDisk(
                showChangeSummary: false,
                status: "Repository selected · physical C# files loaded");
            StatusText.Text = loaded.Project.Folders.SelectMany(folder => folder.Files).Any()
                ? $"Repository selected · loaded {loaded.Scan.Files.Count} physical C# file(s)"
                : "Repository selected · no eligible C# files found";
        }
        catch (Exception exception)
        {
            SetBlankProjectState("Repository selected, but C# source could not be loaded");
            MessageBox.Show(
                this,
                "Code Loom selected the repository but could not load its C# files.\n\n" + exception.Message,
                "Repository load failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveRepositoryBacked_Click(object sender, RoutedEventArgs e)
    {
        SaveEditorToActiveSubfile();
        CommitVariableEdits();
        TrySaveRepositoryProject(showConfirmation: true);
    }

    private void LoadRepositoryBacked_Click(object sender, RoutedEventArgs e)
    {
        if (!HasRepository())
        {
            ChooseRepositoryBacked_Click(sender, e);
            return;
        }

        SaveEditorToActiveSubfile();
        CommitVariableEdits();
        var dirty = _repositoryProject.GetLocallyModifiedSourcePaths(_project);
        if (dirty.Count > 0)
        {
            var shown = string.Join("\n", dirty.Take(8).Select(path => "• " + path));
            var answer = MessageBox.Show(
                this,
                "Reload physical C# files from the repository?\n\nThese Code Loom edits have not been written to their .cs files and would be discarded:\n\n" + shown,
                "Reload repository source",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        try
        {
            LoadRepositoryProjectFromDisk(showChangeSummary: true, status: "Reloaded physical repository C# files");
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "Code Loom could not reload the repository C# files.\n\n" + exception.Message,
                "Repository load failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

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
            foreach (var file in _project.Folders.SelectMany(folder => folder.Files))
                EnsureNewFileHasRepositoryPath(file);

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
