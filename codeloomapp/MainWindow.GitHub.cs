using System.IO;
using System.Windows;
using Microsoft.Win32;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly GitHubCliService _githubCli = new();
    private bool _githubAccountActionBusy;

    private async void SignInGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (_githubAccountActionBusy)
            return;

        _githubAccountActionBusy = true;
        try
        {
            var current = await _githubCli.GetSignedInUserAsync();
            if (current.Success)
            {
                GitHubAccountText.Text = $"GitHub: {current.Message}";
                StatusText.Text = $"GitHub is already connected as {current.Message}";
                return;
            }

            await PromptForGitHubAuthenticationAsync();
        }
        finally
        {
            _githubAccountActionBusy = false;
        }
    }

    private async void CreateGitHubProject_Click(object sender, RoutedEventArgs e)
    {
        if (_githubAccountActionBusy)
            return;

        _githubAccountActionBusy = true;
        try
        {
            StatusText.Text = "Checking GitHub account...";
            var signIn = await _githubCli.GetSignedInUserAsync();
            if (!signIn.Success)
            {
                var connected = await PromptForGitHubAuthenticationAsync();
                if (!connected)
                {
                    StatusText.Text = "GitHub project creation cancelled";
                    return;
                }

                signIn = await _githubCli.GetSignedInUserAsync();
            }

            if (!signIn.Success)
            {
                ShowGitHubAuthenticationFailure(signIn);
                return;
            }

            GitHubAccountText.Text = $"GitHub: {signIn.Message}";

            var projectDialog = new GitHubProjectDialog { Owner = this };
            if (projectDialog.ShowDialog() != true)
            {
                StatusText.Text = "Project creation cancelled";
                return;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = "Choose where Code Loom should create the local project folder"
            };

            if (folderDialog.ShowDialog(this) != true)
            {
                StatusText.Text = "Project creation cancelled";
                return;
            }

            var localFolder = Path.Combine(folderDialog.FolderName, projectDialog.RepositoryName);
            if (Directory.Exists(localFolder) && Directory.EnumerateFileSystemEntries(localFolder).Any())
            {
                MessageBox.Show(
                    this,
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
                    MessageBox.Show(
                        this,
                        FormatGitHubDiagnostic(create),
                        "Repository creation failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    StatusText.Text = $"GitHub repository creation stopped at: {create.Stage}";
                    return;
                }

                _settings.GitRepositoryPath = localFolder;
                _storage.SaveSettings(_settings);
                RefreshRepositoryDisplay();
                SaveStateText.Text = "Saved and pushed";
                StatusText.Text = "Local project and GitHub repository created";

                MessageBox.Show(
                    this,
                    $"Code Loom created the project locally and pushed it to GitHub.\n\nLocal folder:\n{localFolder}",
                    "Project created",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "Code Loom could not create the project.\n\n" + exception.Message,
                    "Project creation failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _githubAccountActionBusy = false;
        }
    }

    private async Task<bool> PromptForGitHubAuthenticationAsync()
    {
        var dialog = new GitHubAuthDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            GitHubAccountText.Text = "GitHub: not signed in";
            StatusText.Text = "GitHub sign-in cancelled";
            return false;
        }

        GitHubCliResult result;
        if (dialog.AuthenticationMethod == GitHubAuthenticationMethod.PersonalAccessToken)
        {
            GitHubAccountText.Text = "GitHub: checking token...";
            StatusText.Text = "Authenticating with the Personal Access Token...";

            var token = dialog.TakeToken();
            try
            {
                result = await _githubCli.SignInWithTokenAsync(token);
            }
            finally
            {
                token = string.Empty;
            }
        }
        else
        {
            GitHubAccountText.Text = "GitHub: browser sign-in in progress...";
            StatusText.Text = "Complete GitHub verification in the opened sign-in window/browser...";
            result = await _githubCli.SignInAsync();

            if (!result.Success
                && string.Equals(result.Stage, "Browser sign-in verification", StringComparison.Ordinal))
            {
                var retry = MessageBox.Show(
                    this,
                    "GitHub accepted the browser/device-code flow, but GitHub CLI did not retain a usable account.\n\n" +
                    "This can happen when Windows credential storage is unavailable or misbehaving. Code Loom can retry the same browser sign-in using GitHub CLI's local file-based credential storage instead.\n\n" +
                    "The credential would stay inside your Windows user profile under Code Loom's GitHub CLI folder. It would not be written into your project or Git repository.\n\nRetry with file-based storage?",
                    "GitHub credential storage fallback",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (retry == MessageBoxResult.Yes)
                {
                    GitHubAccountText.Text = "GitHub: retrying browser sign-in...";
                    StatusText.Text = "Retrying GitHub browser sign-in with local credential storage...";
                    result = await _githubCli.SignInWithBrowserFileStorageAsync();
                }
            }
        }

        if (!result.Success)
        {
            ShowGitHubAuthenticationFailure(result);
            GitHubAccountText.Text = "GitHub: not signed in";
            StatusText.Text = $"GitHub sign-in stopped at: {result.Stage}";
            return false;
        }

        var user = await _githubCli.GetSignedInUserAsync();
        if (!user.Success)
        {
            ShowGitHubAuthenticationFailure(user);
            GitHubAccountText.Text = "GitHub: authentication could not be verified";
            StatusText.Text = "GitHub account verification failed";
            return false;
        }

        GitHubAccountText.Text = $"GitHub: {user.Message}";
        StatusText.Text = $"GitHub connected as {user.Message}";

        if (result.HasWarning)
        {
            MessageBox.Show(
                this,
                result.Warning,
                "GitHub connected — setup warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return true;
    }

    private void ShowGitHubAuthenticationFailure(GitHubCliResult result)
    {
        MessageBox.Show(
            this,
            FormatGitHubDiagnostic(result),
            "GitHub authentication",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string FormatGitHubDiagnostic(GitHubCliResult result)
    {
        var message = string.IsNullOrWhiteSpace(result.Stage)
            ? result.Message
            : $"Stage: {result.Stage}\n\n{result.Message}";

        if (!string.IsNullOrWhiteSpace(result.DiagnosticDetails))
        {
            var detail = result.DiagnosticDetails.Trim();
            if (detail.Length > 2200)
                detail = detail[..2200] + "\n…additional diagnostic text omitted.";

            message += "\n\nTechnical detail:\n" + detail;
        }

        message += "\n\nCode Loom does not include Personal Access Tokens in this diagnostic text or save them in project/settings files.";
        return message;
    }

    private async void RefreshGitHubAccount_Click(object sender, RoutedEventArgs e)
    {
        var user = await _githubCli.GetSignedInUserAsync();
        GitHubAccountText.Text = user.Success
            ? $"GitHub: {user.Message}"
            : "GitHub: not signed in";

        StatusText.Text = user.Success
            ? $"GitHub account verified as {user.Message}"
            : $"GitHub account check failed at: {user.Stage}";
    }
}
