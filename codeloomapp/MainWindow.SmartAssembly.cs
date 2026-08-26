using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using codeloomapp.Models;
using codeloomapp.Services;

namespace codeloomapp;

public partial class MainWindow
{
    private readonly Dictionary<string, MenuItem> _assemblySectionMenuItems = new();
    private ContextMenu? _assemblyContextMenu;
    private MenuItem? _assemblySummaryItem;
    private MenuItem? _assemblyReasonItem;
    private bool _smartAssemblyInitialized;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        InitializeSmartAssemblyUi();
    }

    private void InitializeSmartAssemblyUi()
    {
        if (_smartAssemblyInitialized)
            return;

        _smartAssemblyInitialized = true;

        _assemblyContextMenu = BuildAssemblyContextMenu();
        SubfileList.ContextMenu = _assemblyContextMenu;
        SubfileList.PreviewMouseRightButtonDown += SubfileList_PreviewMouseRightButtonDown;
        SubfileList.SelectionChanged += SmartAssembly_SubfileSelectionChanged;

        ToolTipService.SetToolTip(
            SubfileList,
            "Right-click a subfile to inspect or override its smart assembly placement.");

        UpdateSmartAssemblyStatus();
    }

    private ContextMenu BuildAssemblyContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = BrushFromHex("#121519"),
            Foreground = BrushFromHex("#E8ECF1"),
            BorderBrush = BrushFromHex("#2A3139"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2)
        };

        // Keep native WPF menus consistent with Code Loom's dark palette.
        menu.Resources[SystemColors.MenuBrushKey] = BrushFromHex("#121519");
        menu.Resources[SystemColors.MenuTextBrushKey] = BrushFromHex("#E8ECF1");
        menu.Resources[SystemColors.HighlightBrushKey] = BrushFromHex("#2C353E");
        menu.Resources[SystemColors.HighlightTextBrushKey] = BrushFromHex("#FFFFFF");

        _assemblySummaryItem = new MenuItem
        {
            Header = "Assembly placement",
            IsEnabled = false,
            Foreground = BrushFromHex("#AAB3BD")
        };

        _assemblyReasonItem = new MenuItem
        {
            Header = string.Empty,
            IsEnabled = false,
            FontSize = 10,
            Foreground = BrushFromHex("#76818C")
        };

        var placementMenu = new MenuItem
        {
            Header = "Set assembly placement",
            Foreground = BrushFromHex("#E8ECF1")
        };

        foreach (var section in AssemblySections.All)
        {
            var item = new MenuItem
            {
                Header = string.Equals(section, AssemblySections.Auto, StringComparison.Ordinal)
                    ? "Auto (recommended)"
                    : section,
                Tag = section,
                IsCheckable = true,
                Foreground = BrushFromHex("#E8ECF1")
            };

            item.Click += AssemblySectionMenuItem_Click;
            placementMenu.Items.Add(item);
            _assemblySectionMenuItems[section] = item;
        }

        menu.Items.Add(_assemblySummaryItem);
        menu.Items.Add(_assemblyReasonItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(placementMenu);
        menu.Opened += AssemblyContextMenu_Opened;

        return menu;
    }

    private void SubfileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        var item = ItemsControl.ContainerFromElement(SubfileList, source) as ListBoxItem;
        if (item is not null)
            item.IsSelected = true;
    }

    private void SmartAssembly_SubfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSmartAssemblyStatus();
    }

    private void AssemblyContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateAssemblyMenuState();
    }

    private void AssemblySectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_activeSubfile is null || _activeFile is null || sender is not MenuItem item)
            return;

        if (item.Tag is not string requestedSection)
            return;

        var normalizedSection = CodeAssembler.NormalizeSection(requestedSection);
        _activeSubfile.AssemblySection = normalizedSection;

        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        UpdateSmartAssemblyStatus();
        UpdateAssemblyMenuState();
    }

    private void UpdateSmartAssemblyStatus()
    {
        if (_activeSubfile is null || _activeFile is null)
            return;

        var classification = CodeAssembler.Classify(_activeFile, _activeSubfile);
        var mode = CodeAssembler.NormalizeSection(_activeSubfile.AssemblySection);
        var placement = string.Equals(mode, AssemblySections.Auto, StringComparison.Ordinal)
            ? $"Auto → {classification.Section}"
            : $"{classification.Section} · manual";

        StatusText.Text = $"{_activeSubfile.Name} · Assembly: {placement}";
    }

    private void UpdateAssemblyMenuState()
    {
        var hasSubfile = _activeSubfile is not null && _activeFile is not null;

        if (_assemblySummaryItem is null || _assemblyReasonItem is null)
            return;

        if (!hasSubfile)
        {
            _assemblySummaryItem.Header = "No subfile selected";
            _assemblyReasonItem.Header = string.Empty;

            foreach (var item in _assemblySectionMenuItems.Values)
                item.IsChecked = false;

            return;
        }

        var classification = CodeAssembler.Classify(_activeFile!, _activeSubfile!);
        var mode = CodeAssembler.NormalizeSection(_activeSubfile!.AssemblySection);

        _assemblySummaryItem.Header = string.Equals(mode, AssemblySections.Auto, StringComparison.Ordinal)
            ? $"Detected: {classification.Section}"
            : $"Placed in: {classification.Section} (manual)";
        _assemblyReasonItem.Header = classification.Reason;

        foreach (var pair in _assemblySectionMenuItems)
            pair.Value.IsChecked = string.Equals(pair.Key, mode, StringComparison.Ordinal);
    }
}
