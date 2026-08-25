using System.Windows;

namespace codeloomapp;

public partial class TextPromptDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextPromptDialog(string title, string description, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptTitle.Text = title;
        PromptDescription.Text = description;
        ValueBox.Text = initialValue;
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show(this, "Enter a value first.", "Code Loom", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
