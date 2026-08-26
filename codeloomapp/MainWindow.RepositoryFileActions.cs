using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _repositoryFileActionsInstalled;

    private void EnsureRepositoryFileActionsUi()
    {
        if (_repositoryFileActionsInstalled)
            return;

        var renameButton = FileActionsPanel.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Rename File", StringComparison.Ordinal));
        if (renameButton is not null)
        {
            renameButton.Click -= RenameProjectItem_Click;
            renameButton.Click += RenamePhysicalRepositoryFile_Click;
        }

        var deleteButton = FileActionsPanel.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Delete File", StringComparison.Ordinal));
        if (deleteButton is not null)
        {
            deleteButton.Click -= DeleteProjectItem_Click;
            deleteButton.Click += DeletePhysicalRepositoryFile_Click;
        }

        _repositoryFileActionsInstalled = true;
    }

    private void RenamePhysicalRepositoryFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectItem is not CodeFile file)
            return;

        if (!HasRepository() || file.IsLegacyUnmapped || string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
        {
            RenameProjectItem_Click(sender, e);
            return;
        }

        SaveEditorToActiveSubfile();
        CommitVariableEdits();
        if (!TrySaveRepositoryProject(showConfirmation: false))
            return;

        var dialog = new TextPromptDialog(
            "Rename C# file",
            "Rename the physical repository file. The .cs extension is added automatically.",
            file.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
            return;

        var newName = dialog.Value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? dialog.Value
            : dialog.Value + ".cs";
        if (!NameSafetyService.IsValidWindowsFileName(newName))
        {
            MessageBox.Show(this, "Choose a valid Windows C# file name.", "Rename C# file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var root = Path.GetFullPath(_settings.GitRepositoryPath);
        var oldPath = ResolvePhysicalPath(root, file.RepositoryRelativePath);
        var directory = Path.GetDirectoryName(file.RepositoryRelativePath.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
        var newRelative = RepositoryProjectService.NormalizeRelativePath(Path.Combine(directory, newName));
        var newPath = ResolvePhysicalPath(root, newRelative);

        if (File.Exists(newPath))
        {
            MessageBox.Show(this, "A physical file already exists at that repository path.", "Rename C# file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!File.Exists(oldPath)
            || (!string.IsNullOrWhiteSpace(file.SourceHash)
                && !string.Equals(RepositoryCSharpScanner.HashFile(oldPath), file.SourceHash, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "The physical source changed before the rename. Scan/reload it first; Code Loom left it untouched.", "Rename C# file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        File.Move(oldPath, newPath);
        var oldMeta = oldPath + ".meta";
        var newMeta = newPath + ".meta";
        if (File.Exists(oldMeta) && !File.Exists(newMeta))
            File.Move(oldMeta, newMeta);

        LoadRepositoryProjectFromDisk(showChangeSummary: true, status: $"Renamed physical C# file to {newName}");
        _storage.SaveRepositoryMetadata(_project, _settings.GitRepositoryPath);
    }

    private void DeletePhysicalRepositoryFile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProjectItem is not CodeFile file)
            return;

        if (!HasRepository() || file.IsLegacyUnmapped || string.IsNullOrWhiteSpace(file.RepositoryRelativePath))
        {
            DeleteProjectItem_Click(sender, e);
            return;
        }

        SaveEditorToActiveSubfile();
        CommitVariableEdits();
        if (!TrySaveRepositoryProject(showConfirmation: false))
            return;

        var answer = MessageBox.Show(
            this,
            $"Delete the physical repository file?\n\n{file.RepositoryRelativePath}\n\nThis can be recovered through Git if it has been committed.",
            "Delete C# file",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        var root = Path.GetFullPath(_settings.GitRepositoryPath);
        var fullPath = ResolvePhysicalPath(root, file.RepositoryRelativePath);
        if (!File.Exists(fullPath))
        {
            LoadRepositoryProjectFromDisk(showChangeSummary: true, status: "Physical C# file was already removed");
            return;
        }

        if (!string.IsNullOrWhiteSpace(file.SourceHash)
            && !string.Equals(RepositoryCSharpScanner.HashFile(fullPath), file.SourceHash, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "The physical source changed before deletion. Code Loom left it untouched; scan/reload it first.", "Delete C# file", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        File.Delete(fullPath);
        var metaPath = fullPath + ".meta";
        if (File.Exists(metaPath))
            File.Delete(metaPath);

        LoadRepositoryProjectFromDisk(showChangeSummary: true, status: $"Deleted {file.Name}");
        _storage.SaveRepositoryMetadata(_project, _settings.GitRepositoryPath);
    }

    private static string ResolvePhysicalPath(string root, string relativePath)
    {
        var rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            RepositoryProjectService.NormalizeRelativePath(relativePath)
                .Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Repository path points outside the selected repository.");
        return fullPath;
    }
}
