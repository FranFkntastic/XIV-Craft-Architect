using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using FFXIVCraftArchitect.Services;
using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect;

/// <summary>
/// Interaction logic for LogViewerWindow.xaml
/// Features: Colored log levels, search, filtering, export
/// </summary>
public partial class LogViewerWindow : Window
{
    private List<LogEntry> _allEntries = new();
    private int _currentSearchIndex = -1;
    private readonly List<TextRange> _searchMatches = new();
    private readonly IDialogService _dialogs;

    public LogViewerWindow(DialogServiceFactory dialogFactory)
    {
        InitializeComponent();
        _dialogs = dialogFactory.CreateForWindow(this);
        LoadLogs();
    }

    private void LoadLogs()
    {
        try
        {
            _allEntries.Clear();
            
            if (File.Exists(App.LogFilePath))
            {
                var lines = File.ReadAllLines(App.LogFilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    var entry = ParseLogEntry(lines[i]);
                    entry.LineNumber = i + 1; // 1-based line numbers
                    _allEntries.Add(entry);
                }
            }
            else
            {
                _allEntries.Add(new LogEntry 
                { 
                    Level = LogLevel.Info, 
                    Message = "No debug.log found.",
                    RawText = "No debug.log found."
                });
            }

            ApplyFilterAndDisplay();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _allEntries.Add(new LogEntry 
            { 
                Level = LogLevel.Error, 
                Message = $"Error reading log file: {ex.Message}",
                RawText = $"Error reading log file: {ex.Message}"
            });
            ApplyFilterAndDisplay();
        }
    }

    private LogEntry ParseLogEntry(string line)
    {
        var entry = new LogEntry { RawText = line };

        // Try to extract timestamp and log level
        // Format: [timestamp] [LEVEL] message
        var match = Regex.Match(line, @"^\[([^\]]+)\]\s*\[(\w+)\]\s*(.*)$");
        if (match.Success)
        {
            entry.Timestamp = match.Groups[1].Value;
            entry.Level = ParseLogLevel(match.Groups[2].Value);
            entry.Message = match.Groups[3].Value;
        }
        else
        {
            // Try alternative format: timestamp [LEVEL] message
            match = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2}[\s\w\:\.]+)\s*\[(\w+)\]\s*(.*)$");
            if (match.Success)
            {
                entry.Timestamp = match.Groups[1].Value;
                entry.Level = ParseLogLevel(match.Groups[2].Value);
                entry.Message = match.Groups[3].Value;
            }
            else
            {
                // Default to info level
                entry.Message = line;
                entry.Level = DetermineLevelFromContent(line);
            }
        }

        return entry;
    }

    private LogLevel ParseLogLevel(string level)
    {
        return level.ToUpperInvariant() switch
        {
            "DBG" or "DEBUG" => LogLevel.Debug,
            "INF" or "INFO" or "INFORMATION" => LogLevel.Info,
            "WRN" or "WARN" or "WARNING" => LogLevel.Warning,
            "ERR" or "ERROR" or "FTL" or "FATAL" => LogLevel.Error,
            _ => LogLevel.Info
        };
    }

    private LogLevel DetermineLevelFromContent(string line)
    {
        if (line.Contains("[Error]") || line.Contains("Exception") || line.Contains("Failed"))
            return LogLevel.Error;
        if (line.Contains("[Warning]") || line.Contains("Warning"))
            return LogLevel.Warning;
        if (line.Contains("[Debug]") || line.Contains("Debug"))
            return LogLevel.Debug;
        return LogLevel.Info;
    }

    private void ApplyFilterAndDisplay()
    {
        LogRichTextBox.Document.Blocks.Clear();
        _searchMatches.Clear();

        var filtered = _allEntries.Where(e => ShouldShowEntry(e)).ToList();

        var paragraph = new Paragraph();
        
        foreach (var entry in filtered)
        {
            // Add line number prefix (padded to 6 digits)
            var lineNumberRun = new Run($"[{entry.LineNumber:D6}] ")
            {
                Foreground = Brushes.DarkGray,
                FontSize = 10
            };
            paragraph.Inlines.Add(lineNumberRun);
            
            var run = new Run(entry.RawText)
            {
                Foreground = GetBrushForLevel(entry.Level)
            };
            paragraph.Inlines.Add(run);
            paragraph.Inlines.Add(new LineBreak());
        }

        LogRichTextBox.Document.Blocks.Add(paragraph);

        // Auto-scroll to bottom if enabled
        if (AutoScrollCheck?.IsChecked == true && filtered.Count > 0)
        {
            LogRichTextBox.ScrollToEnd();
        }

        UpdateStatus();
    }

    private bool ShouldShowEntry(LogEntry entry)
    {
        return entry.Level switch
        {
            LogLevel.Debug => ShowDebugCheck?.IsChecked != false,
            LogLevel.Info => ShowInfoCheck?.IsChecked != false,
            LogLevel.Warning => ShowWarningCheck?.IsChecked != false,
            LogLevel.Error => ShowErrorCheck?.IsChecked != false,
            _ => true
        };
    }

    private Brush GetBrushForLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => new SolidColorBrush(Color.FromRgb(136, 136, 136)),   // Gray
            LogLevel.Info => new SolidColorBrush(Color.FromRgb(78, 205, 196)),      // Teal
            LogLevel.Warning => new SolidColorBrush(Color.FromRgb(243, 129, 129)),  // Light red/orange
            LogLevel.Error => new SolidColorBrush(Color.FromRgb(255, 107, 107)),    // Red
            _ => Brushes.LightGray
        };
    }

    private void UpdateStatus()
    {
        var filtered = _allEntries.Where(e => ShouldShowEntry(e)).ToList();
        var lastLine = _allEntries.Count > 0 ? _allEntries.Max(e => e.LineNumber) : 0;
        StatusTextBlock.Text = $"Lines: {lastLine} | Showing {filtered.Count} filtered | Debug: {_allEntries.Count(e => e.Level == LogLevel.Debug)} | Info: {_allEntries.Count(e => e.Level == LogLevel.Info)} | Warnings: {_allEntries.Count(e => e.Level == LogLevel.Warning)} | Errors: {_allEntries.Count(e => e.Level == LogLevel.Error)}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadLogs();
    }

    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (!await _dialogs.ConfirmAsync(
            "Clear the log file? This cannot be undone.",
            "Confirm"))
        {
            return;
        }

        try
        {
            File.WriteAllText(App.LogFilePath, string.Empty);
            LoadLogs();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync($"Failed to clear log: {ex.Message}", ex);
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
            DefaultExt = "txt",
            FileName = "FFXIV_Craft_Architect_Logs.txt"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var filtered = _allEntries.Where(entry => ShouldShowEntry(entry));
                var lines = filtered.Select(e => $"[{e.LineNumber:D6}] {e.RawText}");
                File.WriteAllLines(dialog.FileName, lines);
                await _dialogs.ShowInfoAsync("Logs exported successfully!", "Success");
            }
            catch (Exception ex)
            {
                await _dialogs.ShowErrorAsync($"Failed to export: {ex.Message}", ex);
            }
        }
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        ApplyFilterAndDisplay();
    }

    private void OnWordWrapChanged(object sender, RoutedEventArgs e)
    {
        LogRichTextBox.HorizontalScrollBarVisibility = 
            WordWrapCheck?.IsChecked == true ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            OnFindClick(sender, e);
        }
    }

    private void OnFindClick(object sender, RoutedEventArgs e)
    {
        var searchTerm = SearchTextBox?.Text?.Trim();
        if (string.IsNullOrEmpty(searchTerm))
            return;

        _searchMatches.Clear();
        
        // Find all matches in the document
        var textRange = new TextRange(LogRichTextBox.Document.ContentStart, LogRichTextBox.Document.ContentEnd);
        var text = textRange.Text;
        
        var index = 0;
        while ((index = text.IndexOf(searchTerm, index, StringComparison.OrdinalIgnoreCase)) != -1)
        {
            var start = GetTextPointerAtOffset(LogRichTextBox.Document.ContentStart, index);
            if (start == null) continue;
            
            var end = GetTextPointerAtOffset(start, searchTerm.Length);
            
            if (end != null)
            {
                var matchRange = new TextRange(start, end);
                _searchMatches.Add(matchRange);
            }
            index += searchTerm.Length;
        }

        if (_searchMatches.Count > 0)
        {
            _currentSearchIndex = (_currentSearchIndex + 1) % _searchMatches.Count;
            var match = _searchMatches[_currentSearchIndex];
            HighlightMatch(match);
            
            // Try to find the line number for this match
            var lineNum = GetLineNumberForPosition(match.Start);
            var lineInfo = lineNum > 0 ? $" (Line {lineNum})" : "";
            
            StatusTextBlock.Text = $"Match {_currentSearchIndex + 1} of {_searchMatches.Count}{lineInfo}";
        }
        else
        {
            StatusTextBlock.Text = "No matches found";
        }
    }

    private TextPointer? GetTextPointerAtOffset(TextPointer start, int offset)
    {
        var current = start;
        var count = 0;

        while (current != null && count < offset)
        {
            var next = current.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null) break;
            current = next;
            count++;
        }

        return current;
    }

    /// <summary>
    /// Get the line number for a given position in the log.
    /// Returns the line number from the line number run at the start of the line.
    /// </summary>
    private int GetLineNumberForPosition(TextPointer position)
    {
        try
        {
            // Get the paragraph containing this position
            var paragraph = position.Paragraph;
            if (paragraph == null) return 0;

            // Find the index of this paragraph's line in the document
            var allParagraphs = LogRichTextBox.Document.Blocks.OfType<Paragraph>().ToList();
            var paraIndex = allParagraphs.IndexOf(paragraph);
            if (paraIndex < 0) return 0;

            // Count lines in previous paragraphs
            int lineOffset = 0;
            for (int i = 0; i < paraIndex; i++)
            {
                // Count line breaks in previous paragraphs
                lineOffset += allParagraphs[i].Inlines.OfType<LineBreak>().Count();
            }

            // Count line breaks within this paragraph up to the position
            var inlines = paragraph.Inlines.ToList();
            int inlineIndex = 0;
            for (int i = 0; i < inlines.Count; i++)
            {
                if (inlines[i] is LineBreak)
                {
                    // Check if this line break is before our position
                    if (inlines[i].ContentStart.CompareTo(position) > 0)
                        break;
                    lineOffset++;
                }
            }

            // The line number run is the first inline of each logical line
            // Get the logical line index and look up the entry
            var filtered = _allEntries.Where(e => ShouldShowEntry(e)).ToList();
            if (lineOffset < filtered.Count)
            {
                return filtered[lineOffset].LineNumber;
            }
        }
        catch
        {
            // Ignore errors - just return 0
        }
        
        return 0;
    }

    private void HighlightMatch(TextRange range)
    {
        // Clear previous highlights
        var docRange = new TextRange(LogRichTextBox.Document.ContentStart, LogRichTextBox.Document.ContentEnd);
        docRange.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);

        // Highlight new match
        range.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Yellow);
        range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);

        // Scroll to match
        LogRichTextBox.ScrollToVerticalOffset(range.Start.GetCharacterRect(LogicalDirection.Forward).Top);
        LogRichTextBox.Focus();
    }

    private void OnGoToLineKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            GoToLine();
        }
    }

    private void OnGoToLineClick(object sender, RoutedEventArgs e)
    {
        GoToLine();
    }

    private void GoToLine()
    {
        var lineText = GoToLineTextBox?.Text?.Trim();
        if (string.IsNullOrEmpty(lineText))
            return;

        if (!int.TryParse(lineText, out var targetLine))
        {
            StatusTextBlock.Text = "Invalid line number";
            return;
        }

        // Find the entry with this line number
        var entry = _allEntries.FirstOrDefault(e => e.LineNumber == targetLine);
        if (entry == null)
        {
            StatusTextBlock.Text = $"Line {targetLine} not found";
            return;
        }

        // Check if it's filtered out
        if (!ShouldShowEntry(entry))
        {
            StatusTextBlock.Text = $"Line {targetLine} is filtered out - enable all log levels to view";
            return;
        }

        // Find the paragraph offset for this entry
        var filtered = _allEntries.Where(e => ShouldShowEntry(e)).ToList();
        var index = filtered.FindIndex(e => e.LineNumber == targetLine);
        
        if (index >= 0)
        {
            // Calculate approximate vertical offset (approximate line height * index)
            // This is a rough approximation since RichTextBox doesn't give us exact per-line positions easily
            var lineHeight = 16; // Approximate line height in pixels
            var targetOffset = index * lineHeight;
            
            // Scroll to the position
            LogRichTextBox.ScrollToVerticalOffset(targetOffset);
            
            // Highlight the line briefly
            HighlightLine(index);
            
            StatusTextBlock.Text = $"Jumped to line {targetLine} (entry {index + 1} of {filtered.Count})";
        }
    }

    private void HighlightLine(int lineIndex)
    {
        try
        {
            // Get the paragraph
            var paragraph = LogRichTextBox.Document.Blocks.FirstOrDefault() as Paragraph;
            if (paragraph == null) return;

            // Find the line break at the specified index
            var inlines = paragraph.Inlines.ToList();
            
            // Each "line" consists of: line number run + text run + line break
            // So we need to find the start of our target line
            var inlineIndex = lineIndex * 3; // 3 inlines per displayed line
            
            if (inlineIndex < inlines.Count)
            {
                // Get the text run for this line (index + 1, since +0 is line number)
                var textRun = inlines[inlineIndex + 1] as Run;
                if (textRun != null)
                {
                    // Create a range for this run
                    var start = textRun.ContentStart;
                    var end = textRun.ContentEnd;
                    var range = new TextRange(start, end);
                    
                    // Clear previous highlights
                    var docRange = new TextRange(LogRichTextBox.Document.ContentStart, LogRichTextBox.Document.ContentEnd);
                    docRange.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
                    
                    // Apply highlight
                    range.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Yellow);
                    range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.Black);
                }
            }
        }
        catch
        {
            // Ignore highlighting errors - the scroll worked, that's what matters
        }
    }

    private class LogEntry
    {
        public int LineNumber { get; set; }
        public string RawText { get; set; } = "";
        public string Timestamp { get; set; } = "";
        public LogLevel Level { get; set; } = LogLevel.Info;
        public string Message { get; set; } = "";
    }

    private enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }
}
