using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace codeloomapp;

public partial class NewCodeFileDialog : Window
{
    public string SelectedFolderName => FolderBox.SelectedItem?.ToString() ?? string.Empty;
    public string FileName => EnsureCsExtension(FileNameBox.Text.Trim());
    public string ClassName => ClassNameBox.Text.Trim();
    public string BaseClass => BaseClassBox.Text.Trim();

    public NewCodeFileDialog(IEnumerable<string> folderNames, string? selectedFolderName = null)
    {
        InitializeComponent();

        var names = folderNames.ToList();
        FolderBox.ItemsSource = names;
        FolderBox.SelectedItem = selectedFolderName;

        if (FolderBox.SelectedIndex < 0 && names.Count > 0)
            FolderBox.SelectedIndex = 0;

        FileNameBox.SelectAll();
        FileNameBox.Focus();
    }

    private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || ClassNameBox is null)
            return;

        var proposed = Path.GetFileNameWithoutExtension(FileNameBox.Text.Trim());
        if (!string.IsNullOrWhiteSpace(proposed))
            ClassNameBox.Text = MakeIdentifier(proposed);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (FolderBox.SelectedItem is null)
        {
            MessageBox.Show(this, "Choose a folder for the file.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(FileNameBox.Text) || string.IsNullOrWhiteSpace(ClassNameBox.Text))
        {
            MessageBox.Show(this, "Enter a file name and class name.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!IsValidIdentifier(ClassName))
        {
            MessageBox.Show(this, "The class name must be a valid C# identifier.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static string EnsureCsExtension(string value)
    {
        return value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? value : value + ".cs";
    }

    private static string MakeIdentifier(string value)
    {
        var cleaned = new string(value.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned))
            return "NewScript";

        if (!char.IsLetter(cleaned[0]) && cleaned[0] != '_')
            cleaned = "_" + cleaned;

        return cleaned;
    }

    private static bool IsValidIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!char.IsLetter(value[0]) && value[0] != '_')
            return false;

        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }
}
