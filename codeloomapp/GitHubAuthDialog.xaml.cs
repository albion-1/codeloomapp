using System.Windows;

namespace codeloomapp;

public partial class GitHubAuthDialog : Window
{
    public GitHubAuthenticationMethod AuthenticationMethod { get; private set; }

    public GitHubAuthDialog()
    {
        InitializeComponent();
    }

    public string TakeToken()
    {
        var token = TokenBox.Password;
        TokenBox.Clear();
        return token;
    }

    private void Browser_Click(object sender, RoutedEventArgs e)
    {
        AuthenticationMethod = GitHubAuthenticationMethod.Browser;
        DialogResult = true;
    }

    private void ShowToken_Click(object sender, RoutedEventArgs e)
    {
        ShowTokenButton.Visibility = Visibility.Collapsed;
        TokenPanel.Visibility = Visibility.Visible;
        TokenBox.Focus();
    }

    private void HideToken_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Clear();
        TokenPanel.Visibility = Visibility.Collapsed;
        ShowTokenButton.Visibility = Visibility.Visible;
    }

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TokenBox.Password))
        {
            MessageBox.Show(
                this,
                "Paste a Personal Access Token first.",
                "GitHub token",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            TokenBox.Focus();
            return;
        }

        AuthenticationMethod = GitHubAuthenticationMethod.PersonalAccessToken;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Clear();
        DialogResult = false;
    }
}

public enum GitHubAuthenticationMethod
{
    Browser,
    PersonalAccessToken
}
