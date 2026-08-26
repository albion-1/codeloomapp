using System.Windows;
using System.Windows.Input;

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
        ConnectTokenButton.IsDefault = true;
        TokenBox.Focus();
    }

    private void HideToken_Click(object sender, RoutedEventArgs e)
    {
        TokenBox.Clear();
        ConnectTokenButton.IsDefault = false;
        TokenPanel.Visibility = Visibility.Collapsed;
        ShowTokenButton.Visibility = Visibility.Visible;
        ShowTokenButton.Focus();
    }

    private void TokenBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        SubmitToken();
    }

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        SubmitToken();
    }

    private void SubmitToken()
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
