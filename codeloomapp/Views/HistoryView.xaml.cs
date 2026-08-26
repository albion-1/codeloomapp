using System.Windows;
using System.Windows.Controls;
using codeloomapp.Services;

namespace codeloomapp.Views;

public partial class HistoryView : UserControl
{
    public event EventHandler? UndoRequested;
    public event EventHandler? RedoRequested;
    public event EventHandler<ProjectHistoryEntry>? RestoreRequested;

    public HistoryView()
    {
        InitializeComponent();
    }

    public void LoadHistory(
        IReadOnlyList<ProjectHistoryEntry> entries,
        int currentIndex,
        bool canUndo,
        bool canRedo)
    {
        var newestFirst = entries.Reverse().ToList();
        HistoryList.ItemsSource = newestFirst;

        UndoButton.IsEnabled = canUndo;
        RedoButton.IsEnabled = canRedo;

        if (entries.Count == 0)
        {
            SummaryText.Text = "No history yet";
            return;
        }

        var currentLabel = currentIndex >= 0 && currentIndex < entries.Count
            ? entries[currentIndex].Label
            : "Current project";
        SummaryText.Text = entries.Count == 1
            ? $"1 snapshot · {currentLabel}"
            : $"{entries.Count} snapshots · current: {currentLabel}";
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        UndoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        RedoRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is ProjectHistoryEntry entry)
            RestoreRequested?.Invoke(this, entry);
    }
}
