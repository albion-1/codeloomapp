using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow : Window
{
    private readonly ProjectStorageService _storage = new();
    private readonly GitSyncService _git = new();

    private CodeProject _project = new();
    private AppSettings _settings = new();
    private CodeFile? _activeFile;
    private CodeSubfile? _activeSubfile;
    private bool _isLoadingEditor;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _storage.LoadSettings();

        if (!TryLoadRememberedProject())
            BuildDemoProject();

        RefreshRepositoryDisplay();
        RefreshFileList();

        if (FileList.Items.Count > 0)
            FileList.SelectedIndex = 0;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveEditorToActiveSubfile();
        CommitVariableEdits();

        if (HasRepository())
        {
            try
            {
                _storage.SaveProject(_project, _settings.GitRepositoryPath);
            }
            catch
            {
                // Closing should never be blocked by an autosave failure.
            }
        }

        base.OnClosing(e);
    }

    private bool TryLoadRememberedProject()
    {
        if (!HasRepository())
            return false;

        try
        {
            var loaded = _storage.LoadProject(_settings.GitRepositoryPath);
            if (loaded is null)
                return false;

            _project = loaded;
            SaveStateText.Text = "Loaded from disk";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool HasRepository()
    {
        return !string.IsNullOrWhiteSpace(_settings.GitRepositoryPath)
               && Directory.Exists(_settings.GitRepositoryPath)
               && _git.IsGitRepository(_settings.GitRepositoryPath);
    }

    private void RefreshRepositoryDisplay()
    {
        RepositoryPathText.Text = HasRepository()
            ? _settings.GitRepositoryPath
            : "Not selected";
    }

    private bool SelectRepository()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the local Git repository Code Loom should use"
        };

        if (dialog.ShowDialog() != true)
            return false;

        if (!_git.IsGitRepository(dialog.FolderName))
        {
            MessageBox.Show(
                "That folder is not a Git repository. Choose the folder that contains the .git directory.",
                "Code Loom",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        _settings.GitRepositoryPath = dialog.FolderName;
        _storage.SaveSettings(_settings);
        RefreshRepositoryDisplay();
        return true;
    }

    private void BuildDemoProject()
    {
        _project = new CodeProject { Name = "Wizard Game" };

        var playerFolder = new CodeFolder { Name = "Player" };
        var combatFolder = new CodeFolder { Name = "Combat" };
        var spellsFolder = new CodeFolder { Name = "Spells" };

        var movement = new CodeFile
        {
            Name = "PlayerMovement.cs",
            ClassName = "PlayerMovement",
            BaseClass = "MonoBehaviour"
        };
        movement.UsingStatements.Add("using UnityEngine;");
        movement.Subfiles.Add(new CodeSubfile
        {
            Name = "Movement.Settings",
            Role = "Settings",
            Code = "[SerializeField]\nprivate float walkSpeed = 5f;",
            Receives = "Unity Inspector",
            Returns = "walkSpeed",
            UsedBy = "MovePlayer()",
            Purpose = "Stores the movement speed in one easy-to-find place."
        });
        movement.Subfiles.Add(new CodeSubfile
        {
            Name = "Movement.UnityLifecycle",
            Role = "Unity lifecycle",
            Code = "private void Update()\n{\n    Vector2 input = ReadMovementInput();\n    Vector3 direction = CreateMovementDirection(input);\n\n    MovePlayer(direction);\n}",
            Receives = "Unity frame update",
            Returns = "Nothing",
            UsedBy = "Unity",
            Purpose = "Coordinates the other movement pieces once every frame."
        });
        movement.Subfiles.Add(new CodeSubfile
        {
            Name = "Movement.Input",
            Role = "Input",
            Code = "private Vector2 ReadMovementInput()\n{\n    float horizontal = Input.GetAxisRaw(\"Horizontal\");\n    float vertical = Input.GetAxisRaw(\"Vertical\");\n\n    return new Vector2(horizontal, vertical);\n}",
            Receives = "Keyboard/controller",
            Returns = "Vector2 input",
            UsedBy = "Update()",
            Purpose = "Reads movement controls and returns them as two numbers."
        });
        movement.Subfiles.Add(new CodeSubfile
        {
            Name = "Movement.Direction",
            Role = "Logic",
            Code = "private Vector3 CreateMovementDirection(Vector2 input)\n{\n    return new Vector3(input.x, 0f, input.y).normalized;\n}",
            Receives = "Vector2 input",
            Returns = "Vector3 direction",
            UsedBy = "Update()",
            Purpose = "Converts two-dimensional input into a direction in the 3D world."
        });
        movement.Subfiles.Add(new CodeSubfile
        {
            Name = "Movement.MovePlayer",
            Role = "Action",
            Code = "private void MovePlayer(Vector3 direction)\n{\n    transform.position += direction * walkSpeed * Time.deltaTime;\n}",
            Receives = "Vector3 direction",
            Returns = "Nothing",
            UsedBy = "Update()",
            Purpose = "Actually changes the player object's position."
        });
        movement.Variables.Add(new VariableDefinition
        {
            Name = "walkSpeed",
            Type = "float",
            DefaultValue = "5f",
            DeclaredIn = "Movement.Settings",
            Meaning = "How many Unity units the player moves per second."
        });

        var camera = new CodeFile
        {
            Name = "PlayerCamera.cs",
            ClassName = "PlayerCamera",
            BaseClass = "MonoBehaviour"
        };
        camera.UsingStatements.Add("using UnityEngine;");
        camera.Subfiles.Add(new CodeSubfile
        {
            Name = "Camera.Settings",
            Role = "Settings",
            Code = "[SerializeField]\nprivate float mouseSensitivity = 2f;\n\n[SerializeField]\nprivate float pitchLimit = 70f;",
            Receives = "Unity Inspector",
            Returns = "Camera settings",
            UsedBy = "Camera.Look",
            Purpose = "Stores camera tuning values."
        });
        camera.Subfiles.Add(new CodeSubfile
        {
            Name = "Camera.Look",
            Role = "Logic",
            Code = "private void LookAround()\n{\n    // Mouse look will be designed here later.\n}",
            Receives = "Mouse movement",
            Returns = "Nothing",
            UsedBy = "Update()",
            Purpose = "Placeholder for the eventual mouse-look behavior."
        });
        camera.Variables.Add(new VariableDefinition
        {
            Name = "mouseSensitivity",
            Type = "float",
            DefaultValue = "2f",
            DeclaredIn = "Camera.Settings",
            Meaning = "How strongly mouse movement rotates the camera."
        });
        camera.Variables.Add(new VariableDefinition
        {
            Name = "pitchLimit",
            Type = "float",
            DefaultValue = "70f",
            DeclaredIn = "Camera.Settings",
            Meaning = "Stops the camera from rotating too far vertically."
        });

        var stats = new CodeFile
        {
            Name = "PlayerStats.cs",
            ClassName = "PlayerStats",
            BaseClass = "MonoBehaviour"
        };
        stats.UsingStatements.Add("using UnityEngine;");
        stats.Subfiles.Add(new CodeSubfile
        {
            Name = "Stats.Health",
            Role = "State",
            Code = "[SerializeField]\nprivate int maxHealth = 100;\n\nprivate int currentHealth = 100;",
            Receives = "Inspector/default values",
            Returns = "Health state",
            UsedBy = "Combat systems",
            Purpose = "Stores the player's maximum and current health."
        });
        stats.Variables.Add(new VariableDefinition
        {
            Name = "maxHealth",
            Type = "int",
            DefaultValue = "100",
            DeclaredIn = "Stats.Health",
            Meaning = "The player's maximum health."
        });
        stats.Variables.Add(new VariableDefinition
        {
            Name = "currentHealth",
            Type = "int",
            DefaultValue = "100",
            DeclaredIn = "Stats.Health",
            Meaning = "The player's health right now."
        });

        var battle = new CodeFile
        {
            Name = "BattleController.cs",
            ClassName = "BattleController",
            BaseClass = "MonoBehaviour"
        };
        battle.UsingStatements.Add("using UnityEngine;");
        battle.Subfiles.Add(new CodeSubfile
        {
            Name = "Battle.Start",
            Role = "Lifecycle",
            Code = "private void StartBattle()\n{\n    // Battle setup will be added later.\n}",
            Receives = "Battle request",
            Returns = "Nothing",
            UsedBy = "Game flow",
            Purpose = "Future entry point for starting a battle."
        });

        var spell = new CodeFile
        {
            Name = "Spell.cs",
            ClassName = "Spell",
            BaseClass = "MonoBehaviour"
        };
        spell.UsingStatements.Add("using UnityEngine;");
        spell.Subfiles.Add(new CodeSubfile
        {
            Name = "Spell.Data",
            Role = "Data",
            Code = "// Spell data will live here later.",
            Receives = "Design values",
            Returns = "Spell data",
            UsedBy = "Combat systems",
            Purpose = "Future home for spell information."
        });

        playerFolder.Files.Add(movement);
        playerFolder.Files.Add(camera);
        playerFolder.Files.Add(stats);
        combatFolder.Files.Add(battle);
        spellsFolder.Files.Add(spell);

        _project.Folders.Add(playerFolder);
        _project.Folders.Add(combatFolder);
        _project.Folders.Add(spellsFolder);
    }

    private void RefreshFileList()
    {
        var files = new ObservableCollection<CodeFile>();
        foreach (var folder in _project.Folders)
        {
            foreach (var file in folder.Files)
                files.Add(file);
        }

        FileList.ItemsSource = files;
    }

    private void RefreshEntireProjectUi()
    {
        _activeFile = null;
        _activeSubfile = null;
        RefreshFileList();

        if (FileList.Items.Count > 0)
            FileList.SelectedIndex = 0;
        else
            ClearEditor();
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SaveEditorToActiveSubfile();
        CommitVariableEdits();

        _activeFile = FileList.SelectedItem as CodeFile;
        if (_activeFile is null)
            return;

        ActiveFileText.Text = _activeFile.Name;
        SubfileList.ItemsSource = _activeFile.Subfiles;
        VariablesGrid.ItemsSource = _activeFile.Variables;
        FlowItems.ItemsSource = _activeFile.Subfiles;
        AssembledFileName.Text = _activeFile.Name;
        RefreshAssembledCode();

        if (_activeFile.Subfiles.Count > 0)
            SubfileList.SelectedIndex = 0;

        StatusText.Text = $"Opened {_activeFile.Name}";
    }

    private void SubfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoadingEditor)
            SaveEditorToActiveSubfile();

        _activeSubfile = SubfileList.SelectedItem as CodeSubfile;
        LoadActiveSubfileIntoEditor();
    }

    private void LoadActiveSubfileIntoEditor()
    {
        _isLoadingEditor = true;

        if (_activeSubfile is null)
        {
            ClearEditor();
        }
        else
        {
            SubfileNameBox.Text = _activeSubfile.Name;
            RoleBox.Text = _activeSubfile.Role;
            CodeBox.Text = _activeSubfile.Code;
            ReceivesBox.Text = _activeSubfile.Receives;
            ReturnsBox.Text = _activeSubfile.Returns;
            UsedByBox.Text = _activeSubfile.UsedBy;
            PurposeBox.Text = _activeSubfile.Purpose;
        }

        _isLoadingEditor = false;
    }

    private void ClearEditor()
    {
        SubfileNameBox.Text = string.Empty;
        RoleBox.Text = string.Empty;
        CodeBox.Text = string.Empty;
        ReceivesBox.Text = string.Empty;
        ReturnsBox.Text = string.Empty;
        UsedByBox.Text = string.Empty;
        PurposeBox.Text = string.Empty;
    }

    private void SaveEditorToActiveSubfile()
    {
        if (_isLoadingEditor || _activeSubfile is null)
            return;

        _activeSubfile.Name = string.IsNullOrWhiteSpace(SubfileNameBox.Text)
            ? _activeSubfile.Name
            : SubfileNameBox.Text.Trim();
        _activeSubfile.Role = RoleBox.Text.Trim();
        _activeSubfile.Code = CodeBox.Text;
        _activeSubfile.Receives = ReceivesBox.Text.Trim();
        _activeSubfile.Returns = ReturnsBox.Text.Trim();
        _activeSubfile.UsedBy = UsedByBox.Text.Trim();
        _activeSubfile.Purpose = PurposeBox.Text.Trim();

        SubfileList.Items.Refresh();
        FlowItems.Items.Refresh();
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
    }

    private void CommitVariableEdits()
    {
        VariablesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void RefreshAssembledCode()
    {
        AssembledCodeBox.Text = _activeFile is null
            ? string.Empty
            : CodeAssembler.Assemble(_activeFile);
    }

    private bool SaveProjectToDisk(bool showConfirmation)
    {
        SaveEditorToActiveSubfile();
        CommitVariableEdits();

        if (!HasRepository() && !SelectRepository())
            return false;

        try
        {
            _storage.SaveProject(_project, _settings.GitRepositoryPath);
            SaveStateText.Text = "Saved to disk";
            StatusText.Text = "Project saved to .codeloom/project.json";

            if (showConfirmation)
            {
                MessageBox.Show(
                    $"Project saved to:\n{_storage.GetProjectFilePath(_settings.GitRepositoryPath)}",
                    "Code Loom",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Code Loom could not save the project.\n\n" + exception.Message,
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private void ApplyChanges_Click(object sender, RoutedEventArgs e)
    {
        SaveEditorToActiveSubfile();
        StatusText.Text = _activeSubfile is null
            ? "Nothing selected"
            : $"Applied changes to {_activeSubfile.Name}";
    }

    private void NewSubfile_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null)
            return;

        SaveEditorToActiveSubfile();

        var newSubfile = new CodeSubfile
        {
            Name = "New.Subfile",
            Role = "New part",
            Code = "private void NewMethod()\n{\n    // Add behavior here.\n}",
            Purpose = "Describe this subfile's one clear responsibility."
        };

        _activeFile.Subfiles.Add(newSubfile);
        SubfileList.SelectedItem = newSubfile;
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Created a new virtual subfile";
    }

    private void DeleteSubfile_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _activeSubfile is null)
            return;

        if (_activeFile.Subfiles.Count <= 1)
        {
            StatusText.Text = "A file must keep at least one subfile.";
            return;
        }

        var index = _activeFile.Subfiles.IndexOf(_activeSubfile);
        _activeFile.Subfiles.Remove(_activeSubfile);
        SubfileList.SelectedIndex = Math.Clamp(index - 1, 0, _activeFile.Subfiles.Count - 1);
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Subfile deleted";
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _activeSubfile is null)
            return;

        SaveEditorToActiveSubfile();
        var index = _activeFile.Subfiles.IndexOf(_activeSubfile);
        if (index <= 0)
            return;

        _activeFile.Subfiles.Move(index, index - 1);
        SubfileList.SelectedItem = _activeSubfile;
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Moved subfile up";
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _activeSubfile is null)
            return;

        SaveEditorToActiveSubfile();
        var index = _activeFile.Subfiles.IndexOf(_activeSubfile);
        if (index < 0 || index >= _activeFile.Subfiles.Count - 1)
            return;

        _activeFile.Subfiles.Move(index, index + 1);
        SubfileList.SelectedItem = _activeSubfile;
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Moved subfile down";
    }

    private void AddVariable_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null)
            return;

        _activeFile.Variables.Add(new VariableDefinition
        {
            Name = "newVariable",
            Type = "float",
            DefaultValue = "0f",
            DeclaredIn = _activeSubfile?.Name ?? "Not assigned",
            Meaning = "Explain what this variable represents."
        });

        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Added a variable definition";
    }

    private void ChooseRepository_Click(object sender, RoutedEventArgs e)
    {
        if (!SelectRepository())
            return;

        var existingProject = _storage.LoadProject(_settings.GitRepositoryPath);
        if (existingProject is not null)
        {
            _project = existingProject;
            RefreshEntireProjectUi();
            SaveStateText.Text = "Loaded from disk";
            StatusText.Text = "Git repository selected and existing Code Loom project loaded";
        }
        else
        {
            SaveProjectToDisk(false);
            StatusText.Text = "Git repository selected and current project saved into it";
        }
    }

    private void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        SaveProjectToDisk(true);
    }

    private void LoadProject_Click(object sender, RoutedEventArgs e)
    {
        if (!HasRepository() && !SelectRepository())
            return;

        try
        {
            var loaded = _storage.LoadProject(_settings.GitRepositoryPath);
            if (loaded is null)
            {
                MessageBox.Show(
                    "No .codeloom/project.json exists in this repository yet.",
                    "Code Loom",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _project = loaded;
            RefreshEntireProjectUi();
            SaveStateText.Text = "Loaded from disk";
            StatusText.Text = "Project loaded from disk";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Code Loom could not load the project.\n\n" + exception.Message,
                "Load failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void SyncGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveProjectToDisk(false))
            return;

        StatusText.Text = "Syncing with GitHub...";
        SaveStateText.Text = "Syncing...";

        var result = await _git.SyncAsync(_settings.GitRepositoryPath);
        if (!result.Success)
        {
            SaveStateText.Text = "Sync failed";
            MessageBox.Show(result.Message, "GitHub sync failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "GitHub sync failed";
            return;
        }

        try
        {
            var syncedProject = _storage.LoadProject(_settings.GitRepositoryPath);
            if (syncedProject is not null)
            {
                _project = syncedProject;
                RefreshEntireProjectUi();
            }
        }
        catch
        {
            // The Git operation succeeded; keep the current in-memory project if reload fails.
        }

        SaveStateText.Text = "Synced with GitHub";
        StatusText.Text = result.Message;
    }

    private void ResetDemo_Click(object sender, RoutedEventArgs e)
    {
        _activeFile = null;
        _activeSubfile = null;
        BuildDemoProject();
        RefreshEntireProjectUi();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = "Demo restored";
    }
}
