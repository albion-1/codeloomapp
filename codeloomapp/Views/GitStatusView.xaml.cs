using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using codeloomapp.Services;

namespace codeloomapp.Views;

public partial class GitStatusView : UserControl
{
    public event EventHandler? RefreshRequested;
    public event EventHandler? ContinueRebaseRequested;

    public GitStatusView()
    {
        InitializeComponent();
        DetailsExpander.Expanded += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void SetChecking()
    {
        SummaryText.Text = "Git: checking...";
        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(137, 147, 157));
    }

    public void LoadStatus(GitRepositoryStatus status)
    {
        SummaryText.Text = status.CompactSummary;
        SummaryText.Foreground = new SolidColorBrush(
            status.ConflictCount > 0
                ? Color.FromRgb(226, 163, 163)
                : status.Behind > 0
                    ? Color.FromRgb(212, 178, 121)
                    : status.Changes.Count > 0
                        ? Color.FromRgb(190, 181, 151)
                        : Color.FromRgb(157, 178, 163));

        BranchText.Text = string.IsNullOrWhiteSpace(status.Branch) ? "—" : status.Branch;
        UpstreamText.Text = string.IsNullOrWhiteSpace(status.Upstream) ? "Not connected" : status.Upstream;
        AheadBehindText.Text = $"↑ {status.Ahead}  ·  ↓ {status.Behind}";
        WorkingTreeText.Text = status.ConflictCount > 0
            ? status.ConflictCount == 1 ? "1 conflict" : $"{status.ConflictCount} conflicts"
            : status.Changes.Count == 0
                ? "Clean"
                : status.Changes.Count == 1
                    ? "1 changed file"
                    : $"{status.Changes.Count} changed files";

        RemoteText.Text = string.IsNullOrWhiteSpace(status.RemoteUrl)
            ? "origin: not configured"
            : "origin: " + status.RemoteUrl;

        WarningText.Text = status.WarningMessage;
        WarningText.Visibility = string.IsNullOrWhiteSpace(status.WarningMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ChangesItems.ItemsSource = status.Changes;

        var showConflictPanel = status.ConflictCount > 0 || status.OperationInProgress;
        ConflictPanel.Visibility = showConflictPanel ? Visibility.Visible : Visibility.Collapsed;

        if (status.ConflictCount > 0)
        {
            ConflictTitleText.Text = status.ConflictCount == 1
                ? "1 conflicted file"
                : $"{status.ConflictCount} conflicted files";
            ConflictHelpText.Text =
                "Resolve the files in Visual Studio, stage the resolved files, then continue the rebase here.";
        }
        else if (status.OperationInProgress)
        {
            ConflictTitleText.Text = $"{status.OperationName} in progress";
            ConflictHelpText.Text =
                "Git is waiting for this operation to finish before Code Loom can sync again.";
        }

        ContinueRebaseButton.Visibility = status.OperationInProgress
                                          && string.Equals(status.OperationName, "rebase", StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (status.ConflictCount > 0)
            DetailsExpander.IsExpanded = true;
    }

    public void LoadUnavailable(string message)
    {
        SummaryText.Text = "Git: unavailable";
        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(226, 163, 163));
        BranchText.Text = "—";
        UpstreamText.Text = "—";
        AheadBehindText.Text = "↑ 0  ·  ↓ 0";
        WorkingTreeText.Text = "Unavailable";
        RemoteText.Text = "origin: —";
        ChangesItems.ItemsSource = null;
        ConflictPanel.Visibility = Visibility.Collapsed;
        WarningText.Text = message;
        WarningText.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ContinueRebase_Click(object sender, RoutedEventArgs e)
    {
        ContinueRebaseRequested?.Invoke(this, EventArgs.Empty);
    }
}
