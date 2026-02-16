using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Coordinators;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for the Recipe Planner panel (left + center columns of MainWindow).
/// 
/// DATA FLOW:
/// 1. Project Items (Input):
///    - User adds items via AddProjectItem() from search or import
///    - Stored in ProjectItems ObservableCollection
///    - Each item has: Id, Name, Quantity, IsHqRequired
/// 
/// 2. Plan Building (Processing):
///    - OnBuildProjectPlanAsync() collects ProjectItems
///    - Calls RecipeCalculationService.BuildPlanAsync()
///    - Receives CraftingPlan with populated RootItems
///    - Wraps PlanNodes in PlanNodeViewModels
///    - Syncs to RootNodes ObservableCollection
/// 
/// 3. Node Interactions (User Modification):
///    - User changes AcquisitionSource via dropdown
///    - NodeAcquisitionChanged event bubbles up
///    - Calls SetAcquisitionSource() on the node
///    - Refreshes AggregatedMaterials
///    - Triggers shopping list update
/// 
/// 4. Plan Persistence:
///    - SavePlanAsync() serializes CurrentPlan via PlanPersistenceCoordinator
///    - LoadPlan() deserializes and syncs to UI
/// 
/// UI BINDINGS:
/// - ProjectItems → ProjectList ListBox (left panel)
/// - RootNodes → RecipeTree container (center panel, via RecipeTreeUiBuilder)
/// - CurrentPlan → ShoppingListBuilder (right panel)
/// - StatusMessage → Status bar updates
/// 
/// EVENTS RAISED:
/// - PlanChanged: When CurrentPlan is set/modified
/// - NodeAcquisitionChanged: When user changes source (Craft/Buy/Vendor)
/// - NodeHqChanged: When user toggles HQ requirement
/// </summary>
public partial class RecipePlannerViewModel : ViewModelBase
{
    private CraftingPlan? _currentPlan;
    private ObservableCollection<ProjectItem> _projectItems = new();
    private ObservableCollection<PlanNodeViewModel> _rootNodes = new();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private ILogger<RecipePlannerViewModel>? _logger;
    private bool _diagnosticLoggingEnabled;
    private readonly PlanPersistenceCoordinator _planCoordinator;
    private readonly ExportCoordinator _exportCoordinator;
    private readonly ImportCoordinator _importCoordinator;
    private string _currentPlanPath = string.Empty;

    public RecipePlannerViewModel(
        PlanPersistenceCoordinator planCoordinator,
        ExportCoordinator exportCoordinator,
        ImportCoordinator importCoordinator)
    {
        _planCoordinator = planCoordinator;
        _exportCoordinator = exportCoordinator;
        _importCoordinator = importCoordinator;
        _projectItems.CollectionChanged += OnProjectItemsCollectionChanged;
        _rootNodes.CollectionChanged += OnRootNodesCollectionChanged;
    }

    private void OnProjectItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(ProjectItems));
    }

    private void OnRootNodesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(RootNodes));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _projectItems.CollectionChanged -= OnProjectItemsCollectionChanged;
            _rootNodes.CollectionChanged -= OnRootNodesCollectionChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Sets the logger for diagnostic output.
    /// </summary>
    public void SetLogger(ILogger<RecipePlannerViewModel> logger, bool enableDiagnostics = false)
    {
        _logger = logger;
        _diagnosticLoggingEnabled = enableDiagnostics;
    }

    /// <summary>
    /// Enables or disables diagnostic logging at runtime.
    /// </summary>
    public void SetDiagnosticLoggingEnabled(bool enabled)
    {
        _diagnosticLoggingEnabled = enabled;
    }

    private void LogDiagnostic(string message, params object?[] args)
    {
        if (_diagnosticLoggingEnabled && _logger != null)
        {
            _logger.LogDebug("[DIAGNOSTIC] " + message, args);
        }
    }

    /// <summary>
    /// The current crafting plan being edited.
    /// </summary>
    public CraftingPlan? CurrentPlan
    {
        get => _currentPlan;
        set
        {
            LogDiagnostic("CurrentPlan setter called. Old plan: {OldPlan}, New plan: {NewPlan}",
                _currentPlan?.Name ?? "null", value?.Name ?? "null");
            if (_currentPlan != value)
            {
                _currentPlan = value;
                
                // IMPORTANT: Sync RootNodes BEFORE notifying property changed,
                // because the event handler builds the tree synchronously
                LogDiagnostic("Calling SyncRootNodesFromPlan. RootItems count: {Count}",
                    _currentPlan?.RootItems?.Count ?? 0);
                SyncRootNodesFromPlan();
                LogDiagnostic("SyncRootNodesFromPlan completed. RootNodes count: {Count}", _rootNodes.Count);
                
                // Now notify property changes AFTER RootNodes is populated
                OnPropertyChanged();
                OnPropertyChanged(nameof(AggregatedMaterials));
            }
            else
            {
                LogDiagnostic("CurrentPlan setter - value unchanged, skipping sync");
            }
        }
    }

    /// <summary>
    /// Root-level items in the project (what the user wants to craft).
    /// Uses the ProjectItem class from MainWindow.
    /// </summary>
    public ObservableCollection<ProjectItem> ProjectItems
    {
        get => _projectItems;
        set
        {
            _projectItems = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Root nodes of the recipe tree (derived from CurrentPlan).
    /// </summary>
    public ObservableCollection<PlanNodeViewModel> RootNodes
    {
        get => _rootNodes;
        private set
        {
            _rootNodes = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Aggregated materials from the current plan.
    /// </summary>
    public List<MaterialAggregate> AggregatedMaterials => _currentPlan?.AggregatedMaterials ?? new();

    /// <summary>
    /// Current status message for display.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Whether a loading operation is in progress.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Event raised when a node's acquisition source changes.
    /// </summary>
    public event EventHandler<NodeChangedEventArgs>? NodeAcquisitionChanged;

    /// <summary>
    /// Event raised when a node's HQ requirement changes.
    /// </summary>
    public event EventHandler<NodeChangedEventArgs>? NodeHqChanged;

    /// <summary>
    /// Adds a project item.
    /// </summary>
    public void AddProjectItem(int id, string name, int quantity, bool isHqRequired = false)
    {
        var existing = _projectItems.FirstOrDefault(p => p.Id == id);
        if (existing != null)
        {
            existing.Quantity += quantity;
        }
        else
        {
            _projectItems.Add(new ProjectItem
            {
                Id = id,
                Name = name,
                Quantity = quantity,
                IsHqRequired = isHqRequired
            });
        }
    }

    /// <summary>
    /// Adds a project item from a ProjectItem object (for command binding).
    /// </summary>
    [RelayCommand]
    public void AddProjectItemFromObject(ProjectItem item)
    {
        if (item != null)
        {
            AddProjectItem(item.Id, item.Name, item.Quantity, item.IsHqRequired);
        }
    }

    /// <summary>
    /// Removes a project item by ID.
    /// </summary>
    [RelayCommand]
    public void RemoveProjectItem(int id)
    {
        var item = _projectItems.FirstOrDefault(p => p.Id == id);
        if (item != null)
        {
            _projectItems.Remove(item);
        }
    }

    /// <summary>
    /// Clears all project items and the current plan.
    /// </summary>
    [RelayCommand]
    public void Clear()
    {
        _projectItems.Clear();
        CurrentPlan = null;
    }

    /// <summary>
    /// Sets the acquisition source for a node (for command binding).
    /// </summary>
    [RelayCommand]
    public void SetNodeAcquisitionFromTuple((string nodeId, string source) parameters)
    {
        if (Enum.TryParse<AcquisitionSource>(parameters.source, out var acquisitionSource))
        {
            SetNodeAcquisition(parameters.nodeId, acquisitionSource);
        }
    }

    /// <summary>
    /// Sets the acquisition source for a node (internal method).
    /// </summary>
    public void SetNodeAcquisition(string nodeId, AcquisitionSource source)
    {
        var node = FindNodeById(nodeId);
        if (node != null && node.Source != source)
        {
            node.Source = source;
            NodeAcquisitionChanged?.Invoke(this, new NodeChangedEventArgs(nodeId, node));
        }
    }

    /// <summary>
    /// Toggles the HQ requirement for a node.
    /// </summary>
    public void ToggleNodeHq(string nodeId)
    {
        var node = FindNodeById(nodeId);
        if (node != null && node.CanBeHq)
        {
            node.MustBeHq = !node.MustBeHq;
            NodeHqChanged?.Invoke(this, new NodeChangedEventArgs(nodeId, node));
        }
    }

    /// <summary>
    /// Sets the HQ requirement for a node and optionally propagates to children.
    /// </summary>
    public void SetNodeHq(string nodeId, bool mustBeHq, HqPropagationMode propagationMode = HqPropagationMode.None)
    {
        var node = FindNodeById(nodeId);
        if (node == null || !node.CanBeHq) return;

        node.MustBeHq = mustBeHq;

        if (propagationMode != HqPropagationMode.None)
        {
            PropagateHqRequirement(node, propagationMode, mustBeHq);
        }

        NodeHqChanged?.Invoke(this, new NodeChangedEventArgs(nodeId, node));
    }

    /// <summary>
    /// Expands all nodes in the tree.
    /// </summary>
    [RelayCommand]
    public void ExpandAll()
    {
        SetAllNodesExpanded(true);
    }

    /// <summary>
    /// Collapses all nodes in the tree.
    /// </summary>
    [RelayCommand]
    public void CollapseAll()
    {
        SetAllNodesExpanded(false);
    }

    /// <summary>
    /// Gets the current plan suitable for watch state saving.
    /// </summary>
    public CraftingPlan? GetPlanForWatch()
    {
        return _currentPlan;
    }

    /// <summary>
    /// Restores a plan from watch state.
    /// </summary>
    public void RestoreFromWatch(CraftingPlan plan, List<ProjectItem> projectItems)
    {
        _projectItems.Clear();
        foreach (var item in projectItems)
        {
            _projectItems.Add(item);
        }
        CurrentPlan = plan;
    }

    private void SyncRootNodesFromPlan()
    {
        LogDiagnostic("SyncRootNodesFromPlan started. Current _rootNodes count: {Count}", _rootNodes.Count);
        _rootNodes.Clear();
        LogDiagnostic("_rootNodes cleared");
        if (_currentPlan?.RootItems != null)
        {
            LogDiagnostic("Adding {Count} root items to _rootNodes", _currentPlan.RootItems.Count);
            foreach (var rootItem in _currentPlan.RootItems)
            {
                _rootNodes.Add(new PlanNodeViewModel(rootItem));
                LogDiagnostic("Added root item: {Name} (ID: {ItemId})", rootItem.Name, rootItem.ItemId);
            }
        }
        else
        {
            LogDiagnostic("No root items to add (_currentPlan is null or RootItems is null)");
        }
        LogDiagnostic("Raising PropertyChanged for RootNodes");
        OnPropertyChanged(nameof(RootNodes));
        LogDiagnostic("SyncRootNodesFromPlan completed");
    }

    private PlanNode? FindNodeById(string nodeId)
    {
        if (_currentPlan?.RootItems == null) return null;

        foreach (var root in _currentPlan.RootItems)
        {
            var found = FindNodeRecursive(root, nodeId);
            if (found != null) return found;
        }
        return null;
    }

    private PlanNode? FindNodeRecursive(PlanNode node, string nodeId)
    {
        if (node.NodeId == nodeId) return node;

        foreach (var child in node.Children)
        {
            var found = FindNodeRecursive(child, nodeId);
            if (found != null) return found;
        }
        return null;
    }

    private void PropagateHqRequirement(PlanNode parentNode, HqPropagationMode mode, bool mustBeHq)
    {
        if (mode == HqPropagationMode.AllChildren)
        {
            foreach (var child in parentNode.Children)
            {
                if (child.CanBeHq)
                {
                    child.MustBeHq = mustBeHq;
                }
                PropagateHqRequirement(child, mode, mustBeHq);
            }
        }
        else if (mode == HqPropagationMode.LeafChildren)
        {
            foreach (var child in parentNode.Children)
            {
                if (!child.Children.Any() && child.CanBeHq)
                {
                    child.MustBeHq = mustBeHq;
                }
                else
                {
                    PropagateHqRequirement(child, mode, mustBeHq);
                }
            }
        }
    }

    private void SetAllNodesExpanded(bool isExpanded)
    {
        foreach (var vm in _rootNodes)
        {
            vm.IsExpanded = isExpanded;
        }
    }

    // ========================================================================
    // Plan Persistence Commands
    // ========================================================================

    /// <summary>
    /// Saves the current plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSavePlan))]
    private async Task SavePlanAsync()
    {
        if (_currentPlan == null) return;
        
        var result = await _planCoordinator.SavePlanAsync(
            null!, // Window owner - coordinator handles this via service
            _currentPlan,
            _projectItems.ToList(),
            _currentPlanPath);
        
        if (result.Success)
        {
            _currentPlanPath = result.PlanPath;
        }
        StatusMessage = result.Message;
    }

    /// <summary>
    /// Loads a plan directly (e.g., from native import).
    /// </summary>
    public void LoadPlan(CraftingPlan plan)
    {
        if (plan == null) return;
        
        // Convert root items to project items
        _projectItems.Clear();
        foreach (var rootItem in plan.RootItems)
        {
            _projectItems.Add(new ProjectItem
            {
                Id = rootItem.ItemId,
                Name = rootItem.Name,
                Quantity = rootItem.Quantity,
                IsHqRequired = rootItem.MustBeHq
            });
        }
        
        // Set the new plan
        CurrentPlan = plan;
        
        StatusMessage = $"Loaded plan: {plan.Name}";
    }

    /// <summary>
    /// Loads a plan from the plan browser.
    /// </summary>
    [RelayCommand]
    private async Task LoadPlanAsync()
    {
        var (selected, plan, projectItems) = await _planCoordinator.ShowPlanBrowserAsync(
            null!, // Window owner - coordinator handles this via service
            _currentPlan,
            _projectItems.ToList(),
            _currentPlanPath);
        
        if (selected && plan != null && projectItems != null)
        {
            // Update project items
            _projectItems.Clear();
            foreach (var item in projectItems)
            {
                _projectItems.Add(item);
            }
            
            // Set the new plan
            CurrentPlan = plan;
            
            StatusMessage = $"Loaded plan: {plan.Name}";
        }
    }

    /// <summary>
    /// Renames the current plan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRenamePlan))]
    private async Task RenamePlanAsync()
    {
        var result = await _planCoordinator.RenamePlanAsync(null!, _currentPlanPath);
        
        if (result.Success)
        {
            _currentPlanPath = result.PlanPath;
        }
        StatusMessage = result.Message;
    }

    /// <summary>
    /// Saves the current plan (with check - shows message if no plan).
    /// Always enabled, provides feedback if no plan exists.
    /// </summary>
    [RelayCommand]
    private async Task SavePlanWithCheckAsync()
    {
        if (_currentPlan == null)
        {
            StatusMessage = "No plan to save - build a plan first";
            return;
        }
        
        await SavePlanAsync();
    }

    /// <summary>
    /// Determines if SavePlan command can execute.
    /// </summary>
    private bool CanSavePlan() => _currentPlan != null;

    /// <summary>
    /// Determines if RenamePlan command can execute.
    /// </summary>
    private bool CanRenamePlan() => !string.IsNullOrEmpty(_currentPlanPath);

    // ========================================================================
    // Export Commands
    // ========================================================================

    /// <summary>
    /// Exports the current plan to native Craft Architect format.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportToNativeAsync()
    {
        if (_currentPlan == null) return;
        
        StatusMessage = "Exporting to native format...";
        
        var result = await _exportCoordinator.ExportToNativeFileAsync(_currentPlan);
        
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        
        StatusMessage = result.Message;
    }

    /// <summary>
    /// Exports the current plan to Teamcraft format.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportToTeamcraftAsync()
    {
        if (_currentPlan == null) return;
        
        var result = _exportCoordinator.ExportToTeamcraft(_currentPlan);
        
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusMessage = result.Message;
        }
        else
        {
            StatusMessage = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Exports the current plan to Artisan format.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportToArtisanAsync()
    {
        if (_currentPlan == null) return;
        
        StatusMessage = "Exporting to Artisan format...";
        
        var result = await _exportCoordinator.ExportToArtisanAsync(_currentPlan);
        
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusMessage = result.Message;
        }
        else
        {
            StatusMessage = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Exports the current plan to plain text.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportToPlainTextAsync()
    {
        if (_currentPlan == null) return;
        
        var result = _exportCoordinator.ExportToPlainText(_currentPlan);
        
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusMessage = result.Message;
        }
        else
        {
            StatusMessage = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Exports the current plan to CSV.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportToCsvAsync()
    {
        if (_currentPlan == null) return;
        
        var result = _exportCoordinator.ExportToCsv(_currentPlan);
        
        if (!result.Success)
        {
            StatusMessage = result.Message;
            return;
        }
        
        if (await _exportCoordinator.TrySetClipboardAsync(result.Content))
        {
            StatusMessage = result.Message;
        }
        else
        {
            StatusMessage = "Failed to copy - clipboard may be in use.";
        }
    }

    /// <summary>
    /// Determines if export commands can execute.
    /// </summary>
    private bool CanExport() => _currentPlan != null;

    // ========================================================================
    // Import Commands
    // ========================================================================

    /// <summary>
    /// Imports a plan from Teamcraft format.
    /// </summary>
    [RelayCommand]
    private void ImportFromTeamcraft()
    {
        var result = _importCoordinator.ImportFromTeamcraft(null!, "Aether", "");
        
        if (result.Success && result.Plan != null)
        {
            ApplyImportResult(result);
        }
        StatusMessage = result.Message;
    }

    /// <summary>
    /// Imports a plan from Artisan format.
    /// </summary>
    [RelayCommand]
    private async Task ImportFromArtisanAsync()
    {
        var result = await _importCoordinator.ImportFromArtisanAsync("Aether", "");
        
        if (result.Success && result.Plan != null)
        {
            ApplyImportResult(result);
        }
        StatusMessage = result.Message;
    }

    /// <summary>
    /// Applies import result to ViewModels.
    /// </summary>
    private void ApplyImportResult(ImportCoordinator.ImportResult result)
    {
        if (result.Plan == null || result.ProjectItems == null) return;
        
        CurrentPlan = result.Plan;
        
        _projectItems.Clear();
        foreach (var item in result.ProjectItems)
        {
            _projectItems.Add(item);
        }
    }

}

/// <summary>
/// ViewModel wrapper for a PlanNode that supports expansion state.
/// </summary>
public partial class PlanNodeViewModel : ObservableObject
{
    private readonly PlanNode _node;

    public PlanNodeViewModel(PlanNode node)
    {
        _node = node;
    }

    public PlanNode Node => _node;

    public string NodeId => _node.NodeId;
    public string Name => _node.Name;
    public int Quantity => _node.Quantity;
    public int IconId => _node.IconId;
    public AcquisitionSource Source => _node.Source;
    public bool MustBeHq => _node.MustBeHq;
    public bool CanBeHq => _node.CanBeHq;
    public bool IsBuy => _node.IsBuy;
    public string Job => _node.Job;
    public int RecipeLevel => _node.RecipeLevel;
    public int Yield => _node.Yield;
    public List<PlanNode> Children => _node.Children;
    public bool IsCircularReference => _node.IsCircularReference;
    
    /// <summary>
    /// Whether this item can be bought from a vendor.
    /// </summary>
    public bool CanBuyFromVendor => _node.CanBuyFromVendor;
    
    /// <summary>
    /// Whether this item has a craft recipe and can be crafted.
    /// </summary>
    public bool CanCraft => _node.CanCraft;

    /// <summary>
    /// Full vendor options for this item.
    /// </summary>
    public List<VendorInfo> VendorOptions => _node.VendorOptions;

    /// <summary>
    /// Selected vendor index for procurement.
    /// </summary>
    public int SelectedVendorIndex => _node.SelectedVendorIndex;

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>
/// Event args for node change events.
/// </summary>
public class NodeChangedEventArgs : EventArgs
{
    public string NodeId { get; }
    public PlanNode Node { get; }

    public NodeChangedEventArgs(string nodeId, PlanNode node)
    {
        NodeId = nodeId;
        Node = node;
    }
}

/// <summary>
/// How to propagate HQ requirements to child nodes.
/// </summary>
public enum HqPropagationMode
{
    None,
    AllChildren,
    LeafChildren
}
