using FFXIV_Craft_Architect.Desktop.Services;
using FFXIV_Craft_Architect.Desktop.ViewModels;
using Microsoft.Extensions.Logging;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public sealed class DesktopLogViewerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"craft-architect-log-viewer-{Guid.NewGuid():N}");

    [Fact]
    public void FiltersLoadedLogEntries_BySearchLevelAndCategory()
    {
        var store = new DesktopLogStore(_root);
        store.Append(new DesktopLogEntry(DateTime.UtcNow.AddMinutes(-2), "Debug", "Desktop.Search", 1, "recipe search started", null, null));
        store.Append(new DesktopLogEntry(DateTime.UtcNow.AddMinutes(-1), "Warning", "Desktop.Build", 2, "build warning", "timeout", "stack"));
        var viewModel = CreateViewModel(store);

        Assert.Equal(2, viewModel.FilteredEntries.Count);

        viewModel.SearchText = "recipe";
        var recipeEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("recipe search started", recipeEntry.Message);

        viewModel.SearchText = string.Empty;
        viewModel.SelectedLevelFilter = "Warning";
        var warningEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("build warning", warningEntry.Message);

        viewModel.SelectedLevelFilter = "All";
        viewModel.SelectedCategoryFilter = "Desktop.Search";
        var categoryEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("Desktop.Search", categoryEntry.Category);
    }

    [Fact]
    public void CopySelectedLogEntry_UsesClipboardText()
    {
        var store = new DesktopLogStore(_root);
        store.Append(new DesktopLogEntry(DateTime.UtcNow, "Error", "Desktop.Tests", 1, "copy me", "boom", "stack"));
        var clipboard = new CapturingClipboard();
        var viewModel = CreateViewModel(store, clipboard);

        viewModel.CopySelectedLogEntryCommand.Execute(null);

        Assert.Contains("copy me", clipboard.Text);
        Assert.Contains("boom", clipboard.Text);
    }

    private static DesktopLogViewerViewModel CreateViewModel(
        DesktopLogStore store,
        CapturingClipboard? clipboard = null) =>
        new(
            store,
            new NullFileDialogService(),
            clipboard ?? new CapturingClipboard(),
            new DesktopLogFileShellService(),
            Mock.Of<ILogger<DesktopLogViewerViewModel>>());

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CapturingClipboard : IDesktopClipboardService
    {
        public string Text { get; private set; } = string.Empty;

        public void SetText(string text)
        {
            Text = text;
        }
    }

    private sealed class NullFileDialogService : IDesktopFileDialogService
    {
        public Task<string?> OpenFilePathAsync(IReadOnlyList<string> fileExtensions, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<string?> OpenTextFileAsync(
            string fileTypeLabel,
            IReadOnlyList<string> fileExtensions,
            CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<bool> SaveTextFileAsync(
            string suggestedFileName,
            string content,
            string fileTypeLabel,
            IReadOnlyList<string> fileExtensions,
            CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
