using System.Linq;
using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private CodeFolder? _selectedFolder;
    private object? _selectedProjectItem;
    private bool _projectTreeSubscribed;

    private void ProjectTree_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_projectTreeSubscribed)
        {
            FileList.ItemContainerGenerator.ItemsChanged += (_, _) => RefreshProjectTree();
            _projectTreeSubscribed = true;
        }

        RefreshProjectTree();
    }

    private void RefreshProjectTree()
    {
        if (ProjectTree is null || ProjectNameText is null)
            return;

        ProjectNameText.Text = _project.Name;
        ProjectTree.Items.Clear();

        TreeViewItem? activeFileItem = null;

        foreach (var folder in _project.Folders)
        {
            var folderItem = new TreeViewItem
            {
                Header = folder.Name,
                Tag = folder,
                IsExpanded = true
            };

            foreach (var file in folder.Files)
            {
                var fileItem = new TreeViewItem
                {
                    Header = file.Name,
                    Tag = file
                };

                folderItem.Items.Add(fileItem);

                if (ReferenceEquals(file, _activeFile))
                    activeFileItem = fileItem;
            }

            ProjectTree.Items.Add(folderItem);
        }

        if (activeFileItem is not null)
            activeFileItem.IsSelected = true;
    }

    private void ProjectTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (ProjectTree.SelectedItem is not TreeViewItem item)
            return;

        _selectedProjectItem = item.Tag;

        switch (item.Tag)
        {
            case CodeFolder folder:
                _selectedFolder = folder;
                StatusText.Text = $"Selected folder {folder.Name}";
                break;

            case CodeFile file:
                _selectedFolder = FindFolderContaining(file);
                FileList.SelectedItem = file;
                break;
        }
    }

    private void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog(
            "Rename project",
            "This changes the Code Loom project name stored in project.json.",
            _project.Name)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _project.Name = dialog.Value;
        ProjectNameText.Text = _project.Name;
        MarkProjectStructureChanged($"Renamed project to {_project.Name}");
    }

    private void CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TextPromptDialog(
            "New folder",
            "Folders organize the C# files inside this Code Loom project.",
            "New Folder")
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        if (_project.Folders.Any(folder => string.Equals(folder.Name, dialog.Value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A folder with that name already exists.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var newFolder = new CodeFolder { Name = dialog.Value };
        _project.Folders.Add(newFolder);
        _selectedFolder = newFolder;
        _selectedProjectItem = newFolder;

        RefreshProjectTree();
        MarkProjectStructureChanged($"Created folder {newFolder.Name}");
        SelectTreeObject(newFolder);
    }

    private void CreateFile_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Folders.Count == 0)
        {
            MessageBox.Show(this, "Create a folder before creating a C# file.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedFolderName = _selectedFolder?.Name ?? _project.Folders[0].Name;
        var dialog = new NewCodeFileDialog(_project.Folders.Select(folder => folder.Name), selectedFolderName)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        var folder = _project.Folders.First(candidate => candidate.Name == dialog.SelectedFolderName);
        if (folder.Files.Any(file => string.Equals(file.Name, dialog.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That folder already contains a file with this name.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var file = new CodeFile
        {
            Name = dialog.FileName,
            ClassName = dialog.ClassName,
            BaseClass = string.IsNullOrWhiteSpace(dialog.BaseClass) ? "MonoBehaviour" : dialog.BaseClass
        };

        file.UsingStatements.Add("using UnityEngine;");
        file.Subfiles.Add(new CodeSubfile
        {
            Name = $"{dialog.ClassName}.Main",
            Role = "Starter",
            Code = "// Add this file's first piece of behavior here.",
            Purpose = "Starter subfile for this C# class. Split each responsibility into its own subfile as the class grows."
        });

        folder.Files.Add(file);
        _selectedFolder = folder;
        _selectedProjectItem = file;

        RefreshFileList();
        RefreshProjectTree();
        FileList.SelectedItem = file;
        SelectTreeObject(file);
        MarkProjectStructureChanged($"Created {file.Name}");
    }

    private void RenameProjectItem_Click(object sender, RoutedEventArgs e)
    {
        switch (_selectedProjectItem)
        {
            case CodeFolder folder:
                RenameFolder(folder);
                break;
            case CodeFile file:
                RenameFile(file);
                break;
            default:
                StatusText.Text = "Select a folder or file to rename.";
                break;
        }
    }

    private void RenameFolder(CodeFolder folder)
    {
        var dialog = new TextPromptDialog("Rename folder", "Enter the new folder name.", folder.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value == folder.Name)
            return;

        if (_project.Folders.Any(candidate => !ReferenceEquals(candidate, folder) && string.Equals(candidate.Name, dialog.Value, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A folder with that name already exists.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        folder.Name = dialog.Value;
        RefreshProjectTree();
        MarkProjectStructureChanged($"Renamed folder to {folder.Name}");
        SelectTreeObject(folder);
    }

    private void RenameFile(CodeFile file)
    {
        var folder = FindFolderContaining(file);
        if (folder is null)
            return;

        var oldFileName = file.Name;
        var oldClassName = file.ClassName;
        var dialog = new TextPromptDialog("Rename C# file", "Enter the new file name. The .cs extension is added automatically.", file.Name) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var newName = dialog.Value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? dialog.Value
            : dialog.Value + ".cs";

        if (!NameSafetyService.IsValidWindowsFileName(newName))
        {
            MessageBox.Show(
                this,
                "Use a normal Windows file name without slashes, reserved device names, or a trailing dot/space.",
                "Invalid C# file name",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (folder.Files.Any(candidate => !ReferenceEquals(candidate, file) && string.Equals(candidate.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "That folder already contains a file with this name.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        file.Name = newName;

        var oldStem = Path.GetFileNameWithoutExtension(oldFileName);
        var newStem = Path.GetFileNameWithoutExtension(newName);
        if (string.Equals(oldClassName, oldStem, StringComparison.Ordinal))
        {
            var newClassName = NameSafetyService.MakeSafeCSharpIdentifier(newStem);
            file.ClassName = newClassName;

            // Keep the conventional ClassName.Part subfile labels in sync while
            // preserving any custom labels that the user deliberately chose.
            var oldPrefix = oldClassName + ".";
            foreach (var subfile in file.Subfiles)
            {
                if (subfile.Name.StartsWith(oldPrefix, StringComparison.Ordinal))
                    subfile.Name = newClassName + subfile.Name[oldClassName.Length..];
            }

            foreach (var variable in file.Variables)
            {
                if (variable.DeclaredIn.StartsWith(oldPrefix, StringComparison.Ordinal))
                    variable.DeclaredIn = newClassName + variable.DeclaredIn[oldClassName.Length..];
            }
        }

        RefreshFileList();
        RefreshProjectTree();
        FileList.SelectedItem = file;
        SelectTreeObject(file);
        MarkProjectStructureChanged($"Renamed file to {file.Name}");
    }

    private void DeleteProjectItem_Click(object sender, RoutedEventArgs e)
    {
        switch (_selectedProjectItem)
        {
            case CodeFile file:
                DeleteFile(file);
                break;
            case CodeFolder folder:
                DeleteFolder(folder);
                break;
            default:
                StatusText.Text = "Select a folder or file to delete.";
                break;
        }
    }

    private void DeleteFile(CodeFile file)
    {
        var folder = FindFolderContaining(file);
        if (folder is null)
            return;

        var answer = MessageBox.Show(this,
            $"Delete {file.Name} from the Code Loom project?\n\nThis removes its subfiles and variable definitions from project.json.",
            "Delete file",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
            return;

        var wasActive = ReferenceEquals(_activeFile, file);
        folder.Files.Remove(file);
        _selectedProjectItem = folder;
        _selectedFolder = folder;

        if (wasActive)
            ClearActiveFileSelection();

        RefreshFileList();
        RefreshProjectTree();
        SelectTreeObject(folder);
        MarkProjectStructureChanged($"Deleted {file.Name}");
    }

    private void DeleteFolder(CodeFolder folder)
    {
        var description = folder.Files.Count == 0
            ? $"Delete the folder {folder.Name}?"
            : $"Delete {folder.Name} and all {folder.Files.Count} file(s) inside it?";

        var answer = MessageBox.Show(this, description, "Delete folder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        if (_activeFile is not null && folder.Files.Contains(_activeFile))
            ClearActiveFileSelection();

        _project.Folders.Remove(folder);
        _selectedProjectItem = null;
        _selectedFolder = _project.Folders.FirstOrDefault();

        RefreshFileList();
        RefreshProjectTree();
        MarkProjectStructureChanged($"Deleted folder {folder.Name}");
    }

    private void ClearActiveFileSelection()
    {
        FileList.SelectedItem = null;
        _activeFile = null;
        _activeSubfile = null;
        ActiveFileText.Text = "No file selected";
        AssembledFileName.Text = string.Empty;
        AssembledCodeBox.Text = string.Empty;
        SubfileList.ItemsSource = null;
        VariablesGrid.ItemsSource = null;
        FlowItems.ItemsSource = null;
        ClearEditor();
    }

    private CodeFolder? FindFolderContaining(CodeFile file)
    {
        return _project.Folders.FirstOrDefault(folder => folder.Files.Contains(file));
    }

    private void MarkProjectStructureChanged(string status)
    {
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = status;
    }

    private void SelectTreeObject(object target)
    {
        foreach (var folderObject in ProjectTree.Items)
        {
            if (folderObject is not TreeViewItem folderItem)
                continue;

            if (ReferenceEquals(folderItem.Tag, target))
            {
                folderItem.IsSelected = true;
                folderItem.BringIntoView();
                return;
            }

            foreach (var fileObject in folderItem.Items)
            {
                if (fileObject is TreeViewItem fileItem && ReferenceEquals(fileItem.Tag, target))
                {
                    folderItem.IsExpanded = true;
                    fileItem.IsSelected = true;
                    fileItem.BringIntoView();
                    return;
                }
            }
        }
    }

    private static string MakeSafeClassName(string value)
    {
        return NameSafetyService.MakeSafeCSharpIdentifier(value);
    }
}
