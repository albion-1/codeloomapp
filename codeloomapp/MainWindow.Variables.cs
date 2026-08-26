using System.Windows;
using System.Windows.Controls;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private VariableDefinition? _selectedVariable;
    private bool _isLoadingVariableEditor;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        FileList.SelectionChanged -= FileList_VariableSyncSelectionChanged;
        FileList.SelectionChanged += FileList_VariableSyncSelectionChanged;
        SyncActiveFileVariables();
        InitializeSmartAssemblyUi();
        InitializeUnityExportAndSettingsUi();
    }

    private void FileList_VariableSyncSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncActiveFileVariables();
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs))
            return;

        if (MainTabs.SelectedItem is TabItem tab
            && string.Equals(tab.Header?.ToString(), "Variables", StringComparison.Ordinal))
        {
            SyncActiveFileVariables();
        }
    }

    private void SyncActiveFileVariables(string? selectName = null, string? declaredIn = null)
    {
        if (_activeFile is null)
            return;

        VariableSyncService.SyncFromCode(_activeFile);
        VariablesGrid.ItemsSource = _activeFile.Variables;
        VariablesSummaryText.Text = _activeFile.Variables.Count == 1
            ? "1 class-level field detected from the actual C# source. Local variables are intentionally ignored."
            : $"{_activeFile.Variables.Count} class-level fields detected from the actual C# source. Local variables are intentionally ignored.";

        if (selectName is null)
        {
            if (_selectedVariable is not null)
            {
                var replacement = _activeFile.Variables.FirstOrDefault(variable =>
                    string.Equals(variable.Name, _selectedVariable.Name, StringComparison.Ordinal)
                    && string.Equals(variable.DeclaredIn, _selectedVariable.DeclaredIn, StringComparison.Ordinal));

                VariablesGrid.SelectedItem = replacement;
            }

            return;
        }

        var selected = _activeFile.Variables.FirstOrDefault(variable =>
            string.Equals(variable.Name, selectName, StringComparison.Ordinal)
            && (declaredIn is null
                || string.Equals(variable.DeclaredIn, declaredIn, StringComparison.Ordinal)));

        if (selected is not null)
        {
            VariablesGrid.SelectedItem = selected;
            VariablesGrid.ScrollIntoView(selected);
        }
    }

    private void VariablesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedVariable = VariablesGrid.SelectedItem as VariableDefinition;
        LoadSelectedVariableEditor();
    }

    private void LoadSelectedVariableEditor()
    {
        _isLoadingVariableEditor = true;

        if (_selectedVariable is null)
        {
            VariableEditorExpander.Header = "Variable editor";
            VariableNameBox.Text = string.Empty;
            VariableTypeBox.Text = string.Empty;
            VariableDefaultBox.Text = string.Empty;
            VariableMeaningBox.Text = string.Empty;
            VariableSourceText.Text = "Select a field to edit its declaration.";
            ApplyVariableButton.IsEnabled = false;
            GoToVariableButton.IsEnabled = false;
            DeleteVariableButton.IsEnabled = false;
        }
        else
        {
            VariableEditorExpander.Header = $"Edit {_selectedVariable.Name}";
            VariableNameBox.Text = _selectedVariable.Name;
            VariableTypeBox.Text = _selectedVariable.Type;
            VariableDefaultBox.Text = _selectedVariable.DefaultValue;
            VariableMeaningBox.Text = _selectedVariable.Meaning;

            var accessAndModifiers = string.Join(
                " ",
                new[] { _selectedVariable.Access, _selectedVariable.Modifiers }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            VariableSourceText.Text = string.IsNullOrWhiteSpace(accessAndModifiers)
                ? $"Connected to {_selectedVariable.DeclaredIn}, line {_selectedVariable.SourceLine}."
                : $"Connected to {_selectedVariable.DeclaredIn}, line {_selectedVariable.SourceLine} · {accessAndModifiers}.";

            ApplyVariableButton.IsEnabled = true;
            GoToVariableButton.IsEnabled = true;
            DeleteVariableButton.IsEnabled = true;
        }

        _isLoadingVariableEditor = false;
    }

    private void AddCodeField_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null)
            return;

        var added = VariableSyncService.AddField(_activeFile, _activeSubfile);

        if (ReferenceEquals(added.Subfile, _activeSubfile))
            ReloadActiveSubfileCodeFromModel();

        SubfileList.Items.Refresh();
        RefreshAssembledCode();
        SyncActiveFileVariables(added.VariableName, added.Subfile.Name);
        VariableEditorExpander.IsExpanded = true;
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = $"Added {added.VariableName} to {added.Subfile.Name}";
    }

    private void ApplyVariableToCode_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _selectedVariable is null || _isLoadingVariableEditor)
            return;

        var newName = VariableNameBox.Text;
        var newType = VariableTypeBox.Text;
        var newDefault = VariableDefaultBox.Text;
        var meaning = VariableMeaningBox.Text.Trim();

        _selectedVariable.Meaning = meaning;

        if (!VariableSyncService.TryUpdateField(
                _activeFile,
                _selectedVariable,
                newName,
                newType,
                newDefault,
                out var changedSubfile,
                out var error))
        {
            MessageBox.Show(
                this,
                error,
                "Could not update field",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (ReferenceEquals(changedSubfile, _activeSubfile))
            ReloadActiveSubfileCodeFromModel();

        RefreshAssembledCode();
        SyncActiveFileVariables(newName.Trim(), changedSubfile?.Name);
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = $"Updated {newName.Trim()} in the C# source";
    }

    private void GoToVariableDeclaration_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _selectedVariable is null)
            return;

        var sourceSubfile = _activeFile.Subfiles.FirstOrDefault(subfile =>
            string.Equals(subfile.Name, _selectedVariable.DeclaredIn, StringComparison.Ordinal));

        if (sourceSubfile is null)
            return;

        MainTabs.SelectedIndex = 0;
        SubfileList.SelectedItem = sourceSubfile;
        FocusVariableInCode(_selectedVariable);
    }

    private void DeleteVariableFromCode_Click(object sender, RoutedEventArgs e)
    {
        if (_activeFile is null || _selectedVariable is null)
            return;

        var name = _selectedVariable.Name;
        var confirmation = MessageBox.Show(
            this,
            $"Delete the field '{name}' from the actual C# source?",
            "Delete field",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmation != MessageBoxResult.Yes)
            return;

        if (!VariableSyncService.TryDeleteField(
                _activeFile,
                _selectedVariable,
                out var changedSubfile,
                out var error))
        {
            MessageBox.Show(
                this,
                error,
                "Could not delete field",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (ReferenceEquals(changedSubfile, _activeSubfile))
            ReloadActiveSubfileCodeFromModel();

        _selectedVariable = null;
        RefreshAssembledCode();
        SyncActiveFileVariables();
        LoadSelectedVariableEditor();
        SaveStateText.Text = "Changes not saved to disk";
        StatusText.Text = $"Deleted {name} from the C# source";
    }

    private void ReloadActiveSubfileCodeFromModel()
    {
        if (_activeSubfile is null)
            return;

        _isLoadingEditor = true;
        CodeBox.Text = _activeSubfile.Code;
        _isLoadingEditor = false;
    }

    private void FocusVariableInCode(VariableDefinition variable)
    {
        if (CodeBox.Document is null || CodeBox.Document.LineCount == 0)
            return;

        var lineNumber = Math.Clamp(variable.SourceLine, 1, CodeBox.Document.LineCount);
        var line = CodeBox.Document.GetLineByNumber(lineNumber);
        var lineText = CodeBox.Document.GetText(line);
        var relativeIndex = lineText.IndexOf(variable.Name, StringComparison.Ordinal);

        if (relativeIndex >= 0)
        {
            CodeBox.Select(line.Offset + relativeIndex, variable.Name.Length);
            CodeBox.TextArea.Caret.Offset = line.Offset + relativeIndex + variable.Name.Length;
        }
        else
        {
            CodeBox.TextArea.Caret.Offset = line.Offset;
        }

        CodeBox.ScrollToLine(lineNumber);
        CodeBox.Focus();
        StatusText.Text = $"{variable.Name} is declared in {variable.DeclaredIn}, line {lineNumber}";
    }
}
