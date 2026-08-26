using System.Globalization;
using System.Windows;
using codeloomapp.Services;

namespace codeloomapp;

public partial class SettingsWindow : Window
{
    public double EditorFontSize { get; private set; }
    public bool EditorWordWrap { get; private set; }
    public bool ShowEditorLineNumbers { get; private set; }
    public int AutosaveSeconds { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        EditorFontSizeBox.Text = settings.EditorFontSize.ToString("0.#", CultureInfo.InvariantCulture);
        WordWrapCheckBox.IsChecked = settings.EditorWordWrap;
        LineNumbersCheckBox.IsChecked = settings.ShowEditorLineNumbers;
        AutosaveSecondsBox.Text = settings.AutosaveSeconds.ToString(CultureInfo.InvariantCulture);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Visibility = Visibility.Collapsed;

        if (!double.TryParse(
                EditorFontSizeBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var fontSize)
            || fontSize < 10
            || fontSize > 24)
        {
            ShowValidation("Code font size must be a number from 10 to 24.");
            return;
        }

        if (!int.TryParse(
                AutosaveSecondsBox.Text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var autosaveSeconds)
            || autosaveSeconds < 2
            || autosaveSeconds > 30)
        {
            ShowValidation("Autosave interval must be a whole number from 2 to 30 seconds.");
            return;
        }

        EditorFontSize = fontSize;
        EditorWordWrap = WordWrapCheckBox.IsChecked == true;
        ShowEditorLineNumbers = LineNumbersCheckBox.IsChecked == true;
        AutosaveSeconds = autosaveSeconds;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
        ValidationText.Visibility = Visibility.Visible;
    }
}
