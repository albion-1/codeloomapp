using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using codeloomapp.Services;

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
            ClassNameBox.Text = NameSafetyService.MakeSafeCSharpIdentifier(proposed);
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

        if (!NameSafetyService.IsValidWindowsFileName(FileName))
        {
            MessageBox.Show(
                this,
                "Use a normal Windows file name without slashes, reserved device names, or a trailing dot/space.",
                "Invalid C# file name",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!NameSafetyService.IsValidCSharpIdentifier(ClassName))
        {
            MessageBox.Show(
                this,
                "The class name must be a valid C# identifier and cannot be a C# keyword such as class, int, or namespace.",
                "Invalid class name",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private static string EnsureCsExtension(string value)
    {
        return value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? value : value + ".cs";
    }
}
