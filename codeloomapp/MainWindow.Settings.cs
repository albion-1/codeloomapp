using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace codeloomapp;

public partial class MainWindow
{
    private bool _settingsUiInstalled;

    private void EnsureSettingsUi()
    {
        if (_settingsUiInstalled || Content is not Grid root)
            return;

        var topRow = root.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 0);
        var toolbar = topRow?.Children.OfType<WrapPanel>().FirstOrDefault();
        if (toolbar is null)
            return;

        if (toolbar.Children
            .OfType<Button>()
            .Any(button => string.Equals(
                button.Content?.ToString(),
                "Settings",
                StringComparison.Ordinal)))
        {
            _settingsUiInstalled = true;
            return;
        }

        var button = new Button
        {
            Content = "Settings",
            ToolTip = "Editor and autosave preferences"
        };
        button.Click += Settings_Click;
        toolbar.Children.Add(button);
        _settingsUiInstalled = true;
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        _settings.EditorFontSize = dialog.EditorFontSize;
        _settings.EditorWordWrap = dialog.EditorWordWrap;
        _settings.ShowEditorLineNumbers = dialog.ShowEditorLineNumbers;
        _settings.AutosaveSeconds = dialog.AutosaveSeconds;
        _storage.SaveSettings(_settings);

        ApplyWorkstationSettings();
        StatusText.Text = "Settings saved";
    }

    private void ApplyWorkstationSettings()
    {
        // Clamp old/corrupted settings defensively so a bad settings.json cannot make
        // the editor unusable.
        var fontSize = Math.Clamp(_settings.EditorFontSize, 10, 24);
        var autosaveSeconds = Math.Clamp(_settings.AutosaveSeconds, 2, 30);

        CodeBox.FontSize = fontSize;
        AssembledCodeBox.FontSize = fontSize;
        CodeBox.WordWrap = _settings.EditorWordWrap;
        AssembledCodeBox.WordWrap = _settings.EditorWordWrap;
        CodeBox.ShowLineNumbers = _settings.ShowEditorLineNumbers;
        AssembledCodeBox.ShowLineNumbers = _settings.ShowEditorLineNumbers;

        _autosaveTimer.Interval = TimeSpan.FromSeconds(autosaveSeconds);
    }
}
