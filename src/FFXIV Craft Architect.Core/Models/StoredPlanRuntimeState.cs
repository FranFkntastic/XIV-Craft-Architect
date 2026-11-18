using System.Text.Json;

namespace FFXIV_Craft_Architect.Core.Models;

public sealed record StoredPlanRuntimeState(
    int SchemaVersion,
    IReadOnlyList<StoredPlanNodeRuntimeState> Nodes)
{
    public const int CurrentSchemaVersion = 1;

    public static string Capture(CraftingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nodes = new List<StoredPlanNodeRuntimeState>();
        foreach (var root in plan.RootItems)
        {
            CaptureNode(root, nodes);
        }

        return JsonSerializer.Serialize(
            new StoredPlanRuntimeState(CurrentSchemaVersion, nodes));
    }

    public static void Apply(CraftingPlan plan, string? payload, bool requireComplete = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(payload))
        {
            if (requireComplete)
            {
                throw new InvalidOperationException("Stored acquisition choices are missing.");
            }
            return;
        }

        var state = JsonSerializer.Deserialize<StoredPlanRuntimeState>(payload)
            ?? throw new JsonException("Stored plan runtime state is empty.");
        if (state.SchemaVersion > CurrentSchemaVersion)
        {
            throw new JsonException(
                $"Stored plan runtime state schema {state.SchemaVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }

        var byNodeId = state.Nodes
            .Where(node => !string.IsNullOrWhiteSpace(node.NodeId))
            .GroupBy(node => node.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        if (requireComplete)
        {
            var nodes = new List<StoredPlanNodeRuntimeState>();
            foreach (var root in plan.RootItems)
            {
                CaptureNode(root, nodes);
            }
            if (nodes.Count != byNodeId.Count || nodes.Count != state.Nodes.Count ||
                nodes.Select(n => n.NodeId).Distinct(StringComparer.Ordinal).Count() != nodes.Count ||
                nodes.Any(n => !byNodeId.TryGetValue(n.NodeId, out var stored) || n.ItemId != stored.ItemId))
            {
                throw new InvalidOperationException("Stored acquisition choices do not cover the exact plan nodes.");
            }
        }
        foreach (var root in plan.RootItems)
        {
            ApplyNode(root, byNodeId);
        }
    }

    private static void CaptureNode(
        PlanNode node,
        ICollection<StoredPlanNodeRuntimeState> destination)
    {
        destination.Add(new StoredPlanNodeRuntimeState(
            node.NodeId,
            node.ItemId,
            node.Source,
            node.SourceReason,
            node.MustBeHq,
            node.MarketPrice,
            node.HqMarketPrice,
            node.VendorPrice,
            node.SelectedVendorIndex));
        foreach (var child in node.Children)
        {
            CaptureNode(child, destination);
        }
    }

    private static void ApplyNode(
        PlanNode node,
        IReadOnlyDictionary<string, StoredPlanNodeRuntimeState> stateByNodeId)
    {
        if (stateByNodeId.TryGetValue(node.NodeId, out var state) &&
            state.ItemId == node.ItemId)
        {
            node.Source = state.Source;
            node.SourceReason = state.SourceReason;
            node.MustBeHq = state.MustBeHq;
            node.MarketPrice = state.MarketPrice;
            node.HqMarketPrice = state.HqMarketPrice;
            node.VendorPrice = state.VendorPrice;
            node.SelectedVendorIndex = state.SelectedVendorIndex;
        }

        foreach (var child in node.Children)
        {
            ApplyNode(child, stateByNodeId);
        }
    }
}

public sealed record StoredPlanNodeRuntimeState(
    string NodeId,
    int ItemId,
    AcquisitionSource Source,
    AcquisitionSourceReason SourceReason,
    bool MustBeHq,
    decimal MarketPrice,
    decimal HqMarketPrice,
    decimal VendorPrice,
    int SelectedVendorIndex);
