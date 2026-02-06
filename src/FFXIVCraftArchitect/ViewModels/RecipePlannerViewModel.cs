using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FFXIVCraftArchitect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIVCraftArchitect.ViewModels;

/// <summary>
/// ViewModel for the Recipe Planner panel.
/// Manages project items, recipe tree state, and node interactions.
/// </summary>
public class RecipePlannerViewModel : ViewModelBase
{
    private CraftingPlan? _currentPlan;
    private ObservableCollection<ProjectItem> _projectItems = new();
    private ObservableCollection<PlanNodeViewModel> _rootNodes = new();
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private ILogger<RecipePlannerViewModel>? _logger;
    private bool _diagnosticLoggingEnabled;

    public RecipePlannerViewModel()
    {
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
    /// Removes a project item.
    /// </summary>
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
    public void Clear()
    {
        _projectItems.Clear();
        CurrentPlan = null;
    }

    /// <summary>
    /// Sets the acquisition source for a node.
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
    public void ExpandAll()
    {
        SetAllNodesExpanded(true);
    }

    /// <summary>
    /// Collapses all nodes in the tree.
    /// </summary>
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

}

/// <summary>
/// ViewModel wrapper for a PlanNode that supports expansion state.
/// </summary>
public class PlanNodeViewModel : INotifyPropertyChanged
{
    private readonly PlanNode _node;
    private bool _isExpanded = true;

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

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
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
