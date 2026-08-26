using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using codeloomapp.Models;
using codeloomapp.Services;
using codeloomapp.Views;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly ProjectHistoryService _projectHistory = new();
    private HistoryView? _historyView;
    private bool _secondaryToolsInstalled;

    private void InstallSecondaryToolsUi(StackPanel statusStack)
    {
        if (_secondaryToolsInstalled)
            return;

        _secondaryToolsInstalled = true;

        _historyView = new HistoryView();
        _historyView.UndoRequested += HistoryView_UndoRequested;
        _historyView.RedoRequested += HistoryView_RedoRequested;
        _historyView.RestoreRequested += HistoryView_RestoreRequested;
        statusStack.Children.Add(_historyView);

        InstallImportButton();
        InstallImportDropTarget();
        PreviewKeyDown += MainWindow_ProjectHistoryPreviewKeyDown;
        RefreshHistoryUi();
    }

    private void InitializeProjectHistory()
    {
        var json = _storage.SerializeProject(_project);
        _projectHistory.Initialize(GetHistoryContextKey(), json);
        RefreshHistoryUi();
    }

    private void CaptureProjectHistory(string projectJson, string label)
    {
        _projectHistory.Capture(GetHistoryContextKey(), projectJson, label);
        RefreshHistoryUi();
    }

    private void CaptureImmediateHistory(string label)
    {
        CaptureEditorStateForAutosave();
        var json = _storage.SerializeProject(_project);
        CaptureProjectHistory(json, label);
    }

    private string DescribeHistoryChange()
    {
        if (_activeFile is not null && _activeSubfile is not null)
            return $"Edited {_activeFile.Name} · {_activeSubfile.Name}";

        if (_activeFile is not null)
            return $"Changed {_activeFile.Name}";

        return "Project change";
    }

    private string GetHistoryContextKey()
    {
        return HasRepository()
            ? "repo|" + Path.GetFullPath(_settings.GitRepositoryPath).ToUpperInvariant()
            : "local|" + _project.Name;
    }

    private void EnsureHistoryContext()
    {
        var contextKey = GetHistoryContextKey();
        if (!string.Equals(_projectHistory.ContextKey, contextKey, StringComparison.Ordinal))
            _projectHistory.Initialize(contextKey, _storage.SerializeProject(_project));
    }

    private void RefreshHistoryUi()
    {
        if (_historyView is null)
            return;

        EnsureHistoryContext();
        _historyView.LoadHistory(
            _projectHistory.Entries,
            _projectHistory.CurrentIndex,
            _projectHistory.CanUndo,
            _projectHistory.CanRedo);
    }

    private void HistoryView_UndoRequested(object? sender, EventArgs e)
    {
        UndoProjectHistory();
    }

    private void HistoryView_RedoRequested(object? sender, EventArgs e)
    {
        RedoProjectHistory();
    }

    private void HistoryView_RestoreRequested(object? sender, HistoryRestoreRequestedEventArgs e)
    {
        CaptureEditorStateForAutosave();
        var currentJson = _storage.SerializeProject(_project);
        EnsureHistoryContext();

        var answer = MessageBox.Show(
            this,
            $"Restore this project snapshot?\n\n{e.Entry.Label}\n{e.Entry.TimeLabel}\n\nYour current state stays in project history, so you can return to it with Redo or another snapshot.",
            "Restore project history",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        _projectHistory.Capture(GetHistoryContextKey(), currentJson, DescribeHistoryChange());
        var entry = _projectHistory.Restore(GetHistoryContextKey(), currentJson, e.Entry.Id);
        if (entry is not null)
            ApplyHistoryEntry(entry, "Restored project snapshot");
    }

    private void UndoProjectHistory()
    {
        CaptureEditorStateForAutosave();
        var currentJson = _storage.SerializeProject(_project);
        EnsureHistoryContext();
        _projectHistory.Capture(GetHistoryContextKey(), currentJson, DescribeHistoryChange());

        var entry = _projectHistory.Undo(GetHistoryContextKey(), currentJson);
        if (entry is null)
        {
            StatusText.Text = "No earlier project snapshot to restore";
            RefreshHistoryUi();
            return;
        }

        ApplyHistoryEntry(entry, "Undid project change");
    }

    private void RedoProjectHistory()
    {
        CaptureEditorStateForAutosave();
        var currentJson = _storage.SerializeProject(_project);
        EnsureHistoryContext();

        var entry = _projectHistory.Redo(GetHistoryContextKey(), currentJson);
        if (entry is null)
        {
            StatusText.Text = "No project change to redo";
            RefreshHistoryUi();
            return;
        }

        ApplyHistoryEntry(entry, "Redid project change");
    }

    private void ApplyHistoryEntry(ProjectHistoryEntry entry, string status)
    {
        try
        {
            var restored = _storage.DeserializeProject(entry.ProjectJson);
            if (restored is null)
                return;

            _project = restored;
            _activeFile = null;
            _activeSubfile = null;
            RefreshEntireProjectUi();
            RefreshProjectTree();

            // Force the normal autosave pass to persist the restored state. The
            // history service already points at this snapshot, so this will not
            // create a duplicate history entry.
            _lastAutosavedFingerprint = string.Empty;
            SaveStateText.Text = "Unsaved · history restored";
            StatusText.Text = $"{status}: {entry.Label}";
            RefreshHistoryUi();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                "Code Loom could not restore that project snapshot.\n\n" + exception.Message,
                "Project history",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void MainWindow_ProjectHistoryPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!control)
            return;

        // Keep normal text-editor undo/redo completely intact. Project history only
        // takes over when focus is outside an editable text surface.
        if (CodeBox.IsKeyboardFocusWithin || Keyboard.FocusedElement is TextBoxBase)
            return;

        if (e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            UndoProjectHistory();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Y
            || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            RedoProjectHistory();
            e.Handled = true;
        }
    }

    private void InstallImportButton()
    {
        if (ProjectTree.Parent is not DockPanel projectDock)
            return;

        var header = projectDock.Children
            .OfType<StackPanel>()
            .FirstOrDefault();
        if (header is null)
            return;

        var actions = header.Children
            .OfType<WrapPanel>()
            .FirstOrDefault(panel => panel.Children
                .OfType<Button>()
                .Any(button => string.Equals(
                    button.Content?.ToString(),
                    "+ File",
                    StringComparison.Ordinal)));
        if (actions is null)
            return;

        if (actions.Children
            .OfType<Button>()
            .Any(button => string.Equals(
                button.Content?.ToString(),
                "+ Import C#",
                StringComparison.Ordinal)))
        {
            return;
        }

        var importButton = new Button
        {
            Content = "+ Import C#",
            Padding = new Thickness(7, 4),
            Margin = new Thickness(5, 0, 0, 5)
        };
        importButton.Click += ImportExistingCSharp_Click;
        actions.Children.Add(importButton);
    }

    private void InstallImportDropTarget()
    {
        ProjectTree.AllowDrop = true;
        ProjectTree.PreviewDragOver -= ProjectTree_ImportDragOver;
        ProjectTree.PreviewDragOver += ProjectTree_ImportDragOver;
        ProjectTree.Drop -= ProjectTree_ImportDrop;
        ProjectTree.Drop += ProjectTree_ImportDrop;
    }

    private void ProjectTree_ImportDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files is not null
                    && files.Any(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ProjectTree_ImportDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        var csharpFiles = files
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (csharpFiles.Length == 0)
            return;

        ImportCSharpFiles(csharpFiles);
        e.Handled = true;
    }

    private void ImportExistingCSharp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import existing C# into Code Loom",
            Filter = "C# source files (*.cs)|*.cs",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        ImportCSharpFiles(dialog.FileNames);
    }

    private void ImportCSharpFiles(IEnumerable<string> sourcePaths)
    {
        var paths = sourcePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
            return;

        SaveEditorToActiveSubfile();

        var targetFolder = _selectedFolder ?? _project.Folders.FirstOrDefault();
        if (targetFolder is null)
        {
            targetFolder = new CodeFolder { Name = "Imported" };
            _project.Folders.Add(targetFolder);
            _selectedFolder = targetFolder;
        }

        var importedFiles = new List<CodeFile>();
        var messages = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                var source = File.ReadAllText(path);
                var result = CSharpImportService.Import(source, Path.GetFileName(path));
                result.File.Name = MakeUniqueImportedFileName(targetFolder, result.File.Name);
                targetFolder.Files.Add(result.File);
                importedFiles.Add(result.File);

                foreach (var warning in result.Warnings)
                    messages.Add($"{Path.GetFileName(path)}: {warning}");
            }
            catch (Exception exception)
            {
                messages.Add($"{Path.GetFileName(path)}: {exception.Message}");
            }
        }

        if (importedFiles.Count > 0)
        {
            RefreshFileList();
            RefreshProjectTree();

            var last = importedFiles[^1];
            _selectedFolder = targetFolder;
            _selectedProjectItem = last;
            FileList.SelectedItem = last;
            SelectTreeObject(last);

            CaptureImmediateHistory(importedFiles.Count == 1
                ? $"Imported {last.Name}"
                : $"Imported {importedFiles.Count} C# files");

            SaveStateText.Text = "Changes not saved to disk";
            StatusText.Text = importedFiles.Count == 1
                ? $"Imported {last.Name} into {last.Subfiles.Count} Code Loom subfiles"
                : $"Imported {importedFiles.Count} C# files into {targetFolder.Name}";
        }

        if (messages.Count > 0)
        {
            var shown = string.Join(
                "\n\n",
                messages.Take(8));
            if (messages.Count > 8)
                shown += $"\n\n…and {messages.Count - 8} more messages.";

            MessageBox.Show(
                this,
                shown,
                importedFiles.Count > 0 ? "C# import notes" : "C# import failed",
                MessageBoxButton.OK,
                importedFiles.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
    }

    private static string MakeUniqueImportedFileName(CodeFolder folder, string requestedName)
    {
        if (folder.Files.All(file =>
                !string.Equals(file.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
        {
            return requestedName;
        }

        var stem = Path.GetFileNameWithoutExtension(requestedName);
        var suffix = 2;
        string candidate;
        do
        {
            candidate = $"{stem}.Imported{suffix}.cs";
            suffix++;
        }
        while (folder.Files.Any(file =>
            string.Equals(file.Name, candidate, StringComparison.OrdinalIgnoreCase)));

        return candidate;
    }
}
