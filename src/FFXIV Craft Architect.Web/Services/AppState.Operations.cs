using System.Collections.Frozen;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public partial class AppState
{

    public void NotifyStatusChanged()
    {
        LastStatusUpdate = DateTime.Now;
        PublishChange(AppStateChangeScope.Status, raiseStatusChanged: true);
    }

    /// <summary>
    /// Set status message and optionally show busy state.
    /// </summary>
    public void SetStatus(string message, bool busy = false, double? progress = null)
    {
        StatusMessage = message;
        IsBusy = busy;
        if (progress.HasValue)
        {
            ProgressPercent = Math.Clamp(progress.Value, 0, 100);
        }
        else if (!busy)
        {
            ProgressPercent = 0;
        }
        NotifyStatusChanged();
    }

    /// <summary>
    /// Start a long-running operation with a name.
    /// </summary>
    public AppStateOperation BeginOperation(string operationName, string? message = null)
    {
        var operation = new AppStateOperation(++_nextOperationId, operationName);
        _currentOperationId = operation.Id;
        CurrentOperation = operationName;
        SetStatus(message ?? $"{operationName}...", busy: true);
        return operation;
    }

    /// <summary>
    /// End the current operation.
    /// </summary>
    public void EndOperation(string? message = null)
    {
        _currentOperationId = null;
        CurrentOperation = null;
        IsBusy = false;
        ProgressPercent = 0;
        // Set status directly to avoid any race conditions with progress callbacks
        StatusMessage = message ?? "Ready";
        NotifyStatusChanged();
    }

    public bool EndOperation(AppStateOperation operation, string? message = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        EndOperation(message);
        return true;
    }

    public bool SetStatusForOperation(
        AppStateOperation operation,
        string message,
        bool busy = true,
        double? progress = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        SetStatus(message, busy, progress);
        return true;
    }

    public bool CancelOperation(AppStateOperation operation, string? message = null)
    {
        if (!IsCurrentOperation(operation))
        {
            return false;
        }

        EndOperation(message);
        return true;
    }

    private bool IsCurrentOperation(AppStateOperation operation)
    {
        return _currentOperationId == operation.Id
            && string.Equals(CurrentOperation, operation.Name, StringComparison.Ordinal);
    }

    public void UpdateProgress(double percent, string? message = null)
    {
        ProgressPercent = Math.Clamp(percent, 0, 100);
        if (!string.IsNullOrEmpty(message))
        {
            StatusMessage = message;
        }
        NotifyStatusChanged();
    }

    /// <summary>
    /// Convert shopping items to project items for Recipe Planner
    /// </summary>
    public WorldData? WorldData { get; private set; }

    /// <summary>
    /// Initialize the world data cache.
    /// </summary>
    public Task InitializeWorldDataAsync(
        PackagedWorldDirectoryService packagedWorldDirectory,
        UniversalisService universalisService)
    {
        if (WorldData != null)
        {
            universalisService.SeedWorldData(WorldData);
            return Task.CompletedTask;
        }

        try
        {
            WorldData = packagedWorldDirectory.LoadWorldData();

            if (string.IsNullOrEmpty(SelectedDataCenter))
            {
                SelectedDataCenter = WorldData.DataCenters.FirstOrDefault() ?? "Aether";
            }
        }
        catch
        {
            // Fallback to hardcoded data centers
            WorldData = new WorldData
            {
                DataCenterToWorlds = new Dictionary<string, List<string>>
                {
                    ["Aether"] = new() { "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren" },
                    ["Primal"] = new() { "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros" },
                    ["Crystal"] = new() { "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera" },
                    ["Dynamis"] = new() { "Cuchulainn", "Golem", "Halicarnassus", "Kraken", "Maduin", "Marilith", "Rafflesia", "Seraph" }
                }
            };
            SelectedDataCenter = "Aether";
        }

        universalisService.SeedWorldData(WorldData);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start the auto-save timer.
    /// </summary>
}
