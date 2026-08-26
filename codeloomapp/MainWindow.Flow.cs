using System.Windows.Controls;
using System.Windows.Threading;
using codeloomapp.Services;
using codeloomapp.Views;

namespace codeloomapp;

public partial class MainWindow
{
    private FlowView? _flowView;
    private bool _flowHooksInstalled;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += MainWindow_FlowLoaded;
    }

    private void MainWindow_FlowLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_flowHooksInstalled)
            return;

        _flowHooksInstalled = true;
        MainTabs.SelectionChanged += MainTabs_FlowSelectionChanged;
        FileList.SelectionChanged += FlowSource_SelectionChanged;
        SubfileList.SelectionChanged += FlowSource_SelectionChanged;
        CodeBox.TextChanged += FlowCode_TextChanged;

        if (IsFlowTabSelected())
            ShowFlowView();
    }

    private void MainTabs_FlowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, MainTabs) || !IsFlowTabSelected())
            return;

        ShowFlowView();
    }

    private bool IsFlowTabSelected()
    {
        return MainTabs.SelectedItem is TabItem tab
               && string.Equals(tab.Header?.ToString(), "Flow", StringComparison.Ordinal);
    }

    private void ShowFlowView()
    {
        var flowTab = MainTabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Header?.ToString(), "Flow", StringComparison.Ordinal));

        if (flowTab is null)
            return;

        if (_flowView is null)
        {
            _flowView = new FlowView();
            _flowView.StepRequested += FlowView_StepRequested;
            _flowView.RefreshRequested += FlowView_RefreshRequested;
        }

        if (!ReferenceEquals(flowTab.Content, _flowView))
            flowTab.Content = _flowView;

        RefreshFlowView();
    }

    private void RefreshFlowView()
    {
        if (_flowView is null || _activeFile is null)
            return;

        _flowView.LoadAnalysis(FlowAnalysisService.Analyze(_activeFile));
    }

    private void FlowSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsFlowTabSelected())
            return;

        Dispatcher.BeginInvoke(
            new Action(RefreshFlowView),
            DispatcherPriority.Background);
    }

    private void FlowCode_TextChanged(object? sender, EventArgs e)
    {
        if (!IsFlowTabSelected())
            return;

        Dispatcher.BeginInvoke(
            new Action(RefreshFlowView),
            DispatcherPriority.Background);
    }

    private void FlowView_RefreshRequested(object? sender, EventArgs e)
    {
        RefreshFlowView();
        StatusText.Text = _activeFile is null
            ? "No file selected"
            : $"Refreshed flow for {_activeFile.Name}";
    }

    private void FlowView_StepRequested(object? sender, FlowStepRequestedEventArgs e)
    {
        if (_activeFile is null)
            return;

        MainTabs.SelectedIndex = 0;
        SubfileList.SelectedItem = e.Step.Subfile;

        Dispatcher.BeginInvoke(
            new Action(() => FocusFlowMethod(e.Step)),
            DispatcherPriority.Background);
    }

    private void FocusFlowMethod(FlowStep step)
    {
        var methodName = step.MethodName.EndsWith("()", StringComparison.Ordinal)
            ? step.MethodName[..^2]
            : step.MethodName;

        if (string.IsNullOrWhiteSpace(methodName) || CodeBox.Document is null)
            return;

        var search = methodName + "(";
        var index = CodeBox.Text.IndexOf(search, StringComparison.Ordinal);
        if (index < 0)
            index = CodeBox.Text.IndexOf(methodName, StringComparison.Ordinal);

        if (index >= 0)
        {
            CodeBox.Select(index, methodName.Length);
            CodeBox.TextArea.Caret.Offset = index + methodName.Length;
            var line = CodeBox.Document.GetLineByOffset(index).LineNumber;
            CodeBox.ScrollToLine(line);
        }

        CodeBox.Focus();
        StatusText.Text = $"Flow: {step.MethodName} in {step.SubfileName}";
    }
}
