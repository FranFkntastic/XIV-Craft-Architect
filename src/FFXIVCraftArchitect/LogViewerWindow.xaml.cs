using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

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

    public LogViewerWindow()
    {
        InitializeComponent();
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
                foreach (var line in lines)
                {
                    _allEntries.Add(ParseLogEntry(line));
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
        StatusTextBlock.Text = $"Showing {filtered.Count} of {_allEntries.Count} entries | Debug: {_allEntries.Count(e => e.Level == LogLevel.Debug)} | Info: {_allEntries.Count(e => e.Level == LogLevel.Info)} | Warnings: {_allEntries.Count(e => e.Level == LogLevel.Warning)} | Errors: {_allEntries.Count(e => e.Level == LogLevel.Error)}";
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        LoadLogs();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Clear the log file? This cannot be undone.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            try
            {
                File.WriteAllText(App.LogFilePath, string.Empty);
                LoadLogs();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to clear log: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
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
                File.WriteAllLines(dialog.FileName, filtered.Select(e => e.RawText));
                MessageBox.Show("Logs exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            HighlightMatch(_searchMatches[_currentSearchIndex]);
            StatusTextBlock.Text = $"Match {_currentSearchIndex + 1} of {_searchMatches.Count}";
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

    private class LogEntry
    {
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
