using System.Windows;

namespace codeloomapp;

public partial class GitHubProjectDialog : Window
{
    public GitHubProjectDialog()
    {
        InitializeComponent();
    }

    public string RepositoryName => RepositoryNameBox.Text.Trim();
    public string Description => DescriptionBox.Text.Trim();
    public bool IsPrivate => PrivateRadio.IsChecked == true;

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RepositoryName))
        {
            MessageBox.Show("Enter a project/repository name first.", "Code Loom",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
