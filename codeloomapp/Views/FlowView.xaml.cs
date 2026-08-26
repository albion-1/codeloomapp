using System.Windows;
using System.Windows.Controls;
using codeloomapp.Services;

namespace codeloomapp.Views;

public partial class FlowView : UserControl
{
    public event EventHandler<FlowStepRequestedEventArgs>? StepRequested;
    public event EventHandler? RefreshRequested;

    public FlowView()
    {
        InitializeComponent();
    }

    public void LoadAnalysis(FlowAnalysisResult analysis)
    {
        SummaryText.Text = analysis.Summary;
        PathsItems.ItemsSource = analysis.Paths;
        NodesItems.ItemsSource = analysis.Nodes;
        EmptyFlowText.Visibility = analysis.Paths.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        DependencyDetailsExpander.Header = $"Dependency details ({analysis.Nodes.Count} subfiles)";
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FlowStep step })
            StepRequested?.Invoke(this, new FlowStepRequestedEventArgs(step));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FlowStepRequestedEventArgs : EventArgs
{
    public FlowStepRequestedEventArgs(FlowStep step)
    {
        Step = step;
    }

    public FlowStep Step { get; }
}
