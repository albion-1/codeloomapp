using System.IO;
using System.Windows;
using Microsoft.Win32;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly GitHubCliService _githubCli = new();

    private async void SignInGitHub_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Opening GitHub sign-in...";

        var result = await _githubCli.SignInAsync();
        if (!result.Success)
        {
            MessageBox.Show(result.Message, "GitHub sign-in failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "GitHub sign-in failed";
            return;
        }

        var user = await _githubCli.GetSignedInUserAsync();
        GitHubAccountText.Text = user.Success
            ? $"GitHub: {user.Message}"
            : "GitHub: signed in";

        StatusText.Text = "GitHub connected";
    }

    private async void CreateGitHubProject_Click(object sender, RoutedEventArgs e)
    {
        var signIn = await _githubCli.GetSignedInUserAsync();
        if (!signIn.Success)
        {
            var login = await _githubCli.SignInAsync();
            if (!login.Success)
            {
                MessageBox.Show(login.Message, "GitHub sign-in failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            signIn = await _githubCli.GetSignedInUserAsync();
        }

        if (signIn.Success)
            GitHubAccountText.Text = $"GitHub: {signIn.Message}";

        var projectDialog = new GitHubProjectDialog { Owner = this };
        if (projectDialog.ShowDialog() != true)
            return;

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose where Code Loom should create the local project folder"
        };

        if (folderDialog.ShowDialog() != true)
            return;

        var localFolder = Path.Combine(folderDialog.FolderName, projectDialog.RepositoryName);
        if (Directory.Exists(localFolder) && Directory.EnumerateFileSystemEntries(localFolder).Any())
        {
            MessageBox.Show(
                "That project folder already exists and is not empty. Choose another project name or location.",
                "Code Loom",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(localFolder);

            SaveEditorToActiveSubfile();
            CommitVariableEdits();
            _project.Name = projectDialog.RepositoryName;
            RefreshProjectTree();
            _storage.SaveProject(_project, localFolder);

            StatusText.Text = "Creating GitHub repository...";

            var create = await _githubCli.CreateRepositoryAsync(
                localFolder,
                projectDialog.RepositoryName,
                projectDialog.IsPrivate,
                projectDialog.Description);

            if (!create.Success)
            {
                MessageBox.Show(create.Message, "Repository creation failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "GitHub repository creation failed";
                return;
            }

            _settings.GitRepositoryPath = localFolder;
            _storage.SaveSettings(_settings);
            RefreshRepositoryDisplay();
            SaveStateText.Text = "Saved and pushed";
            StatusText.Text = "Local project and GitHub repository created";

            MessageBox.Show(
                $"Code Loom created the project locally and pushed it to GitHub.\n\nLocal folder:\n{localFolder}",
                "Project created",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Code Loom could not create the project.\n\n" + exception.Message,
                "Project creation failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void RefreshGitHubAccount_Click(object sender, RoutedEventArgs e)
    {
        var user = await _githubCli.GetSignedInUserAsync();
        GitHubAccountText.Text = user.Success
            ? $"GitHub: {user.Message}"
            : "GitHub: not signed in";
    }
}
