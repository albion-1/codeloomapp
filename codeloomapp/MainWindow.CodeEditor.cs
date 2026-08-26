using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace codeloomapp;

public partial class MainWindow
{
    private static IHighlightingDefinition? _softCSharpHighlighting;

    private void CodeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureEditor(CodeBox, readOnly: false);

        CodeBox.TextArea.Caret.PositionChanged -= CodeEditor_CaretPositionChanged;
        CodeBox.TextArea.Caret.PositionChanged += CodeEditor_CaretPositionChanged;

        UpdateEditorStatus();
    }

    private void AssembledCodeEditor_Loaded(object sender, RoutedEventArgs e)
    {
        ConfigureEditor(AssembledCodeBox, readOnly: true);
    }

    private static void ConfigureEditor(TextEditor editor, bool readOnly)
    {
        var editorBackground = BrushFromHex("#0D1013");
        var editorForeground = BrushFromHex("#D7DCE2");

        // AvalonEdit ships with Windows system colors by default, so explicitly
        // theme both the outer control and its inner TextArea to keep Code Loom dark.
        editor.Background = editorBackground;
        editor.Foreground = editorForeground;
        editor.BorderBrush = Brushes.Transparent;
        editor.BorderThickness = new Thickness(0);
        editor.FocusVisualStyle = null;

        editor.TextArea.Background = editorBackground;
        editor.TextArea.Foreground = editorForeground;
        editor.TextArea.FocusVisualStyle = null;

        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.HighlightCurrentLine = !readOnly;

        editor.SyntaxHighlighting = GetSoftCSharpHighlighting();
        editor.LineNumbersForeground = BrushFromHex("#58616B");
        editor.TextArea.SelectionBrush = BrushFromHex("#344554");
        editor.TextArea.SelectionForeground = editorForeground;
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.TextView.CurrentLineBackground = BrushFromHex("#11171D");
        editor.TextArea.TextView.CurrentLineBorder = new Pen(BrushFromHex("#1A222A"), 1);
    }

    private static IHighlightingDefinition? GetSoftCSharpHighlighting()
    {
        if (_softCSharpHighlighting is not null)
            return _softCSharpHighlighting;

        var definition = HighlightingManager.Instance.GetDefinition("C#");
        if (definition is null)
            return null;

        foreach (var color in definition.NamedHighlightingColors)
        {
            var name = color.Name ?? string.Empty;

            color.FontWeight = FontWeights.Normal;

            color.Foreground = name switch
            {
                "Comment" => HighlightBrush("#70806F"),
                "String" => HighlightBrush("#C7A978"),
                "StringInterpolation" => HighlightBrush("#D2C39E"),
                "Char" => HighlightBrush("#C7A978"),
                "Preprocessor" => HighlightBrush("#8FA594"),
                "Punctuation" => HighlightBrush("#838D97"),
                "ValueTypeKeywords" => HighlightBrush("#8FB8C9"),
                "ReferenceTypeKeywords" => HighlightBrush("#8FB8C9"),
                "MethodCall" => HighlightBrush("#D4D8DD"),
                "NumberLiteral" => HighlightBrush("#B5A7C8"),
                "ThisOrBaseReference" => HighlightBrush("#9BB8D0"),
                "NullOrValueKeywords" => HighlightBrush("#9BB8D0"),
                "Keywords" => HighlightBrush("#88AFC9"),
                "GotoKeywords" => HighlightBrush("#88AFC9"),
                "ContextKeywords" => HighlightBrush("#9BB8D0"),
                "ExceptionKeywords" => HighlightBrush("#A4B4CB"),
                "CheckedKeyword" => HighlightBrush("#A4B4CB"),
                "UnsafeKeywords" => HighlightBrush("#A4B4CB"),
                "OperatorKeywords" => HighlightBrush("#A9A3BF"),
                "ParameterModifiers" => HighlightBrush("#A9A3BF"),
                "Modifiers" => HighlightBrush("#A4B4CB"),
                "Visibility" => HighlightBrush("#88AFC9"),
                "NamespaceKeywords" => HighlightBrush("#91AF9C"),
                "GetSetAddRemove" => HighlightBrush("#A4B4CB"),
                "TrueFalse" => HighlightBrush("#9BB8D0"),
                "TypeKeywords" => HighlightBrush("#8FB8C9"),
                "SemanticKeywords" => HighlightBrush("#9BB8D0"),
                _ => color.Foreground
            };
        }

        _softCSharpHighlighting = definition;
        return definition;
    }

    private static SimpleHighlightingBrush HighlightBrush(string hex)
    {
        return new SimpleHighlightingBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void CodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_isLoadingEditor || _activeSubfile is null)
        {
            UpdateEditorStatus();
            return;
        }

        _activeSubfile.Code = CodeBox.Text;
        RefreshAssembledCode();
        SaveStateText.Text = "Changes not saved to disk";
        UpdateEditorStatus();
    }

    private void CodeEditor_CaretPositionChanged(object? sender, EventArgs e)
    {
        UpdateEditorStatus();
    }

    private void UpdateEditorStatus()
    {
        if (EditorPositionText is null || CodeBox.Document is null)
            return;

        var location = CodeBox.Document.GetLocation(CodeBox.CaretOffset);
        EditorPositionText.Text = $"Ln {location.Line}, Col {location.Column}   ·   {CodeBox.Document.LineCount} lines";
    }

    private void OpenFind_Click(object sender, RoutedEventArgs e)
    {
        ShowFindPanel(focusReplace: false);
    }

    private void CloseFind_Click(object sender, RoutedEventArgs e)
    {
        SearchToolsPanel.Visibility = Visibility.Collapsed;
        CodeBox.Focus();
    }

    private void ShowFindPanel(bool focusReplace)
    {
        SearchToolsPanel.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(CodeBox.SelectedText))
            FindTextBox.Text = CodeBox.SelectedText;

        if (focusReplace)
        {
            ReplaceTextBox.Focus();
            ReplaceTextBox.SelectAll();
        }
        else
        {
            FindTextBox.Focus();
            FindTextBox.SelectAll();
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        FindMatch(backwards: false);
    }

    private void FindPrevious_Click(object sender, RoutedEventArgs e)
    {
        FindMatch(backwards: true);
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindMatch(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SearchToolsPanel.Visibility = Visibility.Collapsed;
            CodeBox.Focus();
            e.Handled = true;
        }
    }

    private bool FindMatch(bool backwards)
    {
        var query = FindTextBox.Text;
        var text = CodeBox.Text;

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            FindStateText.Text = "Type something to find";
            return false;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        int index;

        if (backwards)
        {
            var start = Math.Max(0, CodeBox.SelectionStart - 1);
            index = text.LastIndexOf(query, start, comparison);
            if (index < 0)
                index = text.LastIndexOf(query, comparison);
        }
        else
        {
            var start = Math.Min(text.Length, CodeBox.SelectionStart + CodeBox.SelectionLength);
            index = text.IndexOf(query, start, comparison);
            if (index < 0)
                index = text.IndexOf(query, comparison);
        }

        if (index < 0)
        {
            FindStateText.Text = "No matches";
            return false;
        }

        CodeBox.Select(index, query.Length);
        CodeBox.TextArea.Caret.Offset = index + query.Length;
        CodeBox.ScrollToLine(CodeBox.Document.GetLineByOffset(index).LineNumber);
        FindStateText.Text = "Match found";
        return true;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        if (!string.Equals(CodeBox.SelectedText, query, StringComparison.OrdinalIgnoreCase))
        {
            if (!FindMatch(backwards: false))
                return;
        }

        CodeBox.Document.Replace(CodeBox.SelectionStart, CodeBox.SelectionLength, ReplaceTextBox.Text);
        FindMatch(backwards: false);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var source = CodeBox.Text;
        var replacement = ReplaceTextBox.Text;
        var comparison = StringComparison.OrdinalIgnoreCase;
        var positions = new List<int>();
        var searchFrom = 0;

        while (searchFrom <= source.Length - query.Length)
        {
            var index = source.IndexOf(query, searchFrom, comparison);
            if (index < 0)
                break;

            positions.Add(index);
            searchFrom = index + query.Length;
        }

        if (positions.Count == 0)
        {
            FindStateText.Text = "No matches";
            return;
        }

        for (var index = positions.Count - 1; index >= 0; index--)
            CodeBox.Document.Replace(positions[index], query.Length, replacement);

        FindStateText.Text = $"Replaced {positions.Count}";
    }

    private void CodeEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (control && e.Key == Key.F)
        {
            ShowFindPanel(focusReplace: false);
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.H)
        {
            ShowFindPanel(focusReplace: true);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F3)
        {
            FindMatch(backwards: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
            return;
        }

        if (control && e.Key == Key.Oem2)
        {
            ToggleLineComment();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SearchToolsPanel.Visibility == Visibility.Visible)
        {
            SearchToolsPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void ToggleLineComment()
    {
        if (CodeBox.Document is null || CodeBox.Document.TextLength == 0)
            return;

        var selectionStart = CodeBox.SelectionLength > 0 ? CodeBox.SelectionStart : CodeBox.CaretOffset;
        var selectionEnd = CodeBox.SelectionLength > 0
            ? CodeBox.SelectionStart + CodeBox.SelectionLength
            : CodeBox.CaretOffset;

        var firstLine = CodeBox.Document.GetLineByOffset(Math.Min(selectionStart, CodeBox.Document.TextLength));
        var finalOffset = Math.Max(0, Math.Min(selectionEnd, CodeBox.Document.TextLength) - (CodeBox.SelectionLength > 0 ? 1 : 0));
        var lastLine = CodeBox.Document.GetLineByOffset(finalOffset);

        var lines = new List<ICSharpCode.AvalonEdit.Document.DocumentLine>();
        var current = firstLine;
        while (current is not null)
        {
            lines.Add(current);
            if (current == lastLine)
                break;
            current = current.NextLine;
        }

        var allCommented = lines
            .Where(line => line.Length > 0)
            .All(line => CodeBox.Document.GetText(line).TrimStart().StartsWith("//", StringComparison.Ordinal));

        for (var index = lines.Count - 1; index >= 0; index--)
        {
            var line = lines[index];
            var lineText = CodeBox.Document.GetText(line);
            var leading = lineText.Length - lineText.TrimStart().Length;

            if (allCommented)
            {
                var markerIndex = lineText.IndexOf("//", leading, StringComparison.Ordinal);
                if (markerIndex >= 0)
                    CodeBox.Document.Remove(line.Offset + markerIndex, 2);
            }
            else
            {
                CodeBox.Document.Insert(line.Offset + leading, "// ");
            }
        }
    }
}
