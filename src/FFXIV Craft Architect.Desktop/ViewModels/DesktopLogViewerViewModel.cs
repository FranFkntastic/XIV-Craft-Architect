using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIV_Craft_Architect.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.Desktop.ViewModels;

public sealed partial class DesktopLogViewerViewModel : ObservableObject
{
    private readonly DesktopLogStore _logStore;
    private readonly IDesktopFileDialogService _fileDialogs;
    private readonly IDesktopClipboardService _clipboard;
    private readonly DesktopLogFileShellService _fileShell;
    private readonly ILogger<DesktopLogViewerViewModel> _logger;
    private readonly List<DesktopLogRow> _loadedEntries = new();
    private bool _suppressSelectedFileReload;

    [ObservableProperty]
    private DesktopLogFileRow? _selectedLogFile;

    [ObservableProperty]
    private DesktopLogRow? _selectedLogEntry;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevelFilter = "All";

    [ObservableProperty]
    private string _selectedCategoryFilter = "All";

    [ObservableProperty]
    private string _viewerStatusText = "No log file loaded.";

    [ObservableProperty]
    private string _selectedFilePathText = "No log file selected.";

    [ObservableProperty]
    private string _entryCountText = "0 entries";

    public ObservableCollection<DesktopLogFileRow> LogFiles { get; } = new();

    public ObservableCollection<string> LevelFilters { get; } = new(["All", "Trace", "Debug", "Information", "Warning", "Error", "Critical"]);

    public ObservableCollection<string> CategoryFilters { get; } = new(["All"]);

    public ObservableCollection<DesktopLogRow> FilteredEntries { get; } = new();

    public DesktopLogViewerViewModel(
        DesktopLogStore logStore,
        IDesktopFileDialogService fileDialogs,
        IDesktopClipboardService clipboard,
        DesktopLogFileShellService fileShell,
        ILogger<DesktopLogViewerViewModel> logger)
    {
        _logStore = logStore ?? throw new ArgumentNullException(nameof(logStore));
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _fileShell = fileShell ?? throw new ArgumentNullException(nameof(fileShell));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        RefreshLogFiles();
    }

    [RelayCommand]
    private void RefreshLogFiles()
    {
        var selectedPath = SelectedLogFile?.FullPath;
        _suppressSelectedFileReload = true;
        LogFiles.Clear();
        foreach (var path in _logStore.ListLogFiles())
        {
            LogFiles.Add(DesktopLogFileRow.FromPath(path, isExternal: false));
        }

        SelectedLogFile = LogFiles.FirstOrDefault(file =>
            string.Equals(file.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase))
            ?? LogFiles.FirstOrDefault(file =>
                string.Equals(file.FullPath, _logStore.LogPath, StringComparison.OrdinalIgnoreCase))
            ?? LogFiles.FirstOrDefault();
        _suppressSelectedFileReload = false;
        LoadSelectedLogFile();
        _logger.LogDebug("Log viewer refreshed available log files. Count={Count}", LogFiles.Count);
    }

    [RelayCommand]
    private async Task OpenDifferentLogAsync()
    {
        var path = await _fileDialogs.OpenFilePathAsync([".jsonl", ".log", ".txt"]);
        if (string.IsNullOrWhiteSpace(path))
        {
            ViewerStatusText = "Open log cancelled.";
            return;
        }

        var existing = LogFiles.FirstOrDefault(file =>
            string.Equals(file.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = DesktopLogFileRow.FromPath(path, isExternal: true);
            LogFiles.Insert(0, existing);
        }

        SelectedLogFile = existing;
        _logger.LogInformation("Log viewer opened external log file {Path}.", path);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedLevelFilter = "All";
        SelectedCategoryFilter = "All";
        ApplyFilters();
        _logger.LogTrace("Log viewer filters cleared.");
    }

    [RelayCommand]
    private void OpenSelectedLogFile()
    {
        if (SelectedLogFile == null)
        {
            ViewerStatusText = "Select a log file before opening it.";
            return;
        }

        _fileShell.OpenDefault(SelectedLogFile.FullPath);
        ViewerStatusText = $"Opened {SelectedLogFile.FileName}.";
        _logger.LogInformation("Opened selected log file {Path}.", SelectedLogFile.FullPath);
    }

    [RelayCommand]
    private void OpenSelectedLogFileWith()
    {
        if (SelectedLogFile == null)
        {
            ViewerStatusText = "Select a log file before choosing an app.";
            return;
        }

        _fileShell.OpenWith(SelectedLogFile.FullPath);
        ViewerStatusText = $"Choose an app for {SelectedLogFile.FileName}.";
        _logger.LogInformation("Opened Windows Open With for log file {Path}.", SelectedLogFile.FullPath);
    }

    [RelayCommand]
    private void RevealSelectedLogFile()
    {
        if (SelectedLogFile == null)
        {
            ViewerStatusText = "Select a log file before revealing it.";
            return;
        }

        _fileShell.RevealInExplorer(SelectedLogFile.FullPath);
        ViewerStatusText = $"Revealed {SelectedLogFile.FileName}.";
        _logger.LogInformation("Revealed selected log file {Path}.", SelectedLogFile.FullPath);
    }

    [RelayCommand]
    private void CopySelectedLogEntry()
    {
        if (SelectedLogEntry == null)
        {
            ViewerStatusText = "Select a log entry before copying it.";
            return;
        }

        _clipboard.SetText(SelectedLogEntry.CopyText);
        ViewerStatusText = "Copied selected log entry.";
        _logger.LogTrace("Copied selected log entry from viewer.");
    }

    partial void OnSelectedLogFileChanged(DesktopLogFileRow? value)
    {
        if (!_suppressSelectedFileReload)
        {
            LoadSelectedLogFile();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilters();

    partial void OnSelectedLevelFilterChanged(string value) => ApplyFilters();

    partial void OnSelectedCategoryFilterChanged(string value) => ApplyFilters();

    private void LoadSelectedLogFile()
    {
        _loadedEntries.Clear();
        FilteredEntries.Clear();
        SelectedLogEntry = null;

        if (SelectedLogFile == null)
        {
            SelectedFilePathText = "No log file selected.";
            ViewerStatusText = "No log file loaded.";
            EntryCountText = "0 entries";
            RebuildCategoryFilters();
            return;
        }

        SelectedFilePathText = SelectedLogFile.FullPath;
        foreach (var entry in _logStore.LoadAll(SelectedLogFile.FullPath))
        {
            _loadedEntries.Add(new DesktopLogRow(
                entry.Timestamp.ToLocalTime(),
                entry.Level,
                entry.Category,
                entry.EventId,
                entry.Message,
                entry.Exception,
                entry.StackTrace));
        }

        RebuildCategoryFilters();
        ApplyFilters();
        ViewerStatusText = $"Loaded {SelectedLogFile.FileName}.";
        _logger.LogDebug(
            "Loaded log viewer file {Path}. EntryCount={EntryCount}",
            SelectedLogFile.FullPath,
            _loadedEntries.Count);
    }

    private void RebuildCategoryFilters()
    {
        var selected = SelectedCategoryFilter;
        CategoryFilters.Clear();
        CategoryFilters.Add("All");
        foreach (var category in _loadedEntries
            .Select(entry => entry.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(category => category))
        {
            CategoryFilters.Add(category);
        }

        SelectedCategoryFilter = CategoryFilters.Contains(selected) ? selected : "All";
    }

    private void ApplyFilters()
    {
        var query = SearchText.Trim();
        var rows = _loadedEntries.Where(entry =>
            MatchesLevel(entry)
            && MatchesCategory(entry)
            && MatchesSearch(entry, query));

        FilteredEntries.Clear();
        foreach (var row in rows)
        {
            FilteredEntries.Add(row);
        }

        SelectedLogEntry = FilteredEntries.FirstOrDefault();
        EntryCountText = $"{FilteredEntries.Count:N0} of {_loadedEntries.Count:N0} entries";
    }

    private bool MatchesLevel(DesktopLogRow entry) =>
        SelectedLevelFilter == "All"
        || string.Equals(entry.Level, SelectedLevelFilter, StringComparison.OrdinalIgnoreCase);

    private bool MatchesCategory(DesktopLogRow entry) =>
        SelectedCategoryFilter == "All"
        || string.Equals(entry.Category, SelectedCategoryFilter, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesSearch(DesktopLogRow entry, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return Contains(entry.Level, query)
            || Contains(entry.Category, query)
            || Contains(entry.Message, query)
            || Contains(entry.Exception, query)
            || Contains(entry.StackTrace, query);
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record DesktopLogFileRow(string FullPath, bool IsExternal)
{
    public string FileName => Path.GetFileName(FullPath);

    public string ModifiedText => File.Exists(FullPath)
        ? File.GetLastWriteTime(FullPath).ToString("g")
        : "Missing";

    public string SizeText => File.Exists(FullPath)
        ? $"{new FileInfo(FullPath).Length / 1024.0:N1} KB"
        : "0 KB";

    public string SourceText => IsExternal ? "External" : "Desktop";

    public string MetadataText => $"{SourceText} | {ModifiedText} | {SizeText}";

    public static DesktopLogFileRow FromPath(string path, bool isExternal) =>
        new(path, isExternal);
}
