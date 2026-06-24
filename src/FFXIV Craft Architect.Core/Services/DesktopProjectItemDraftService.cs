using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class DesktopProjectItemDraftService
{
    private static readonly DesktopKnownProjectItem[] Catalog =
    [
        new(5107, "Cobalt Plate", 1998, true),
        new(5395, "Ancient Lumber", 999, true),
        new(5099, "Cobalt Rivets", 1998, true),
        new(6141, "Darksteel Nugget", 1498, true)
    ];

    public DesktopPlanMutationResult AddTarget(
        CraftSessionState session,
        string? itemName,
        string? quantityText,
        bool mustBeHq)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return new DesktopPlanMutationResult(false, "Enter an item name before adding a target.");
        }

        var quantity = ParseQuantity(quantityText);
        if (quantity <= 0)
        {
            return new DesktopPlanMutationResult(false, "Quantity must be at least 1.");
        }

        var definition = FindKnownItem(itemName);
        if (definition == null)
        {
            return new DesktopPlanMutationResult(false, "Search and add a Garland result before adding an unknown target.");
        }

        var target = CreateProjectItem(definition, quantity, mustBeHq);

        return AddOrUpdateTarget(session, target, quantity, mustBeHq);
    }

    public DesktopPlanMutationResult AddTarget(
        CraftSessionState session,
        int itemId,
        string itemName,
        string? quantityText,
        bool mustBeHq)
    {
        if (itemId <= 0 || string.IsNullOrWhiteSpace(itemName))
        {
            return new DesktopPlanMutationResult(false, "Select a valid search result before adding a target.");
        }

        var quantity = ParseQuantity(quantityText);
        if (quantity <= 0)
        {
            return new DesktopPlanMutationResult(false, "Quantity must be at least 1.");
        }

        var definition = FindKnownItem(itemName);
        var target = definition?.ItemId == itemId
            ? CreateProjectItem(definition, quantity, mustBeHq)
            : CreateCustomProjectItem(itemId, itemName.Trim(), quantity, mustBeHq);

        return AddOrUpdateTarget(session, target, quantity, mustBeHq);
    }

    public DesktopPlanMutationResult RemoveTarget(CraftSessionState session, int itemId)
    {
        if (session.ProjectItems.Count == 0)
        {
            return new DesktopPlanMutationResult(false, "No draft target list is loaded.");
        }

        var items = session.ProjectItems.Select(CloneProjectItem).ToList();
        var removed = items.RemoveAll(item => item.Id == itemId);
        if (removed == 0)
        {
            return new DesktopPlanMutationResult(false, "Select a draft target before removing it.");
        }

        ActivateDraft(session, ResolveDraftName(session), items, "desktop target removed");
        return new DesktopPlanMutationResult(true, "Target removed from the draft target list.");
    }

    public DesktopPlanMutationResult AdjustRootQuantity(CraftSessionState session, int itemId, int delta)
    {
        if (session.ProjectItems.Count == 0)
        {
            return new DesktopPlanMutationResult(false, "No draft target list is loaded.");
        }

        var items = session.ProjectItems.Select(CloneProjectItem).ToList();
        var target = items.FirstOrDefault(item => item.Id == itemId);
        if (target == null)
        {
            return new DesktopPlanMutationResult(false, "Select a draft target before changing quantity.");
        }

        target.Quantity = Math.Max(1, target.Quantity + delta);
        ActivateDraft(session, ResolveDraftName(session), items, $"desktop quantity adjusted: {target.Name}");
        return new DesktopPlanMutationResult(true, $"{target.Name} quantity is now {target.Quantity:N0}.");
    }

    public DesktopPlanMutationResult ToggleRootHq(CraftSessionState session, int itemId)
    {
        if (session.ProjectItems.Count == 0)
        {
            return new DesktopPlanMutationResult(false, "No draft target list is loaded.");
        }

        var items = session.ProjectItems.Select(CloneProjectItem).ToList();
        var target = items.FirstOrDefault(item => item.Id == itemId);
        if (target == null)
        {
            return new DesktopPlanMutationResult(false, "Select a draft target before toggling HQ.");
        }

        target.MustBeHq = !target.MustBeHq;
        ActivateDraft(session, ResolveDraftName(session), items, $"desktop HQ toggled: {target.Name}");
        return new DesktopPlanMutationResult(true, $"{target.Name} quality set to {(target.MustBeHq ? "HQ" : "NQ")}.");
    }

    private static DesktopPlanMutationResult AddOrUpdateTarget(
        CraftSessionState session,
        ProjectItem target,
        int quantity,
        bool mustBeHq)
    {
        var items = session.ProjectItems.Select(CloneProjectItem).ToList();
        var existing = items.FirstOrDefault(item => item.Id == target.Id);
        if (existing != null)
        {
            existing.Quantity += quantity;
            existing.MustBeHq |= mustBeHq;
        }
        else
        {
            items.Add(target);
        }

        ActivateDraft(session, ResolveDraftName(session), items, $"desktop target added: {target.Name}");
        return new DesktopPlanMutationResult(true, $"{target.Name} added to the draft target list.");
    }

    public IReadOnlyList<DesktopKnownProjectItem> SearchKnownItems(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return [];
        }

        var query = itemName.Trim();
        return Catalog
            .Where(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name)
            .ToList();
    }

    private static DesktopKnownProjectItem? FindKnownItem(string itemName) =>
        Catalog.FirstOrDefault(item => item.Name.Equals(itemName.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? Catalog.FirstOrDefault(item => item.Name.Contains(itemName.Trim(), StringComparison.OrdinalIgnoreCase));

    private static int ParseQuantity(string? quantityText) =>
        int.TryParse(quantityText, out var quantity) ? quantity : 0;

    private static void ActivateDraft(
        CraftSessionState session,
        string draftName,
        IReadOnlyList<ProjectItem> projectItems,
        string reason)
    {
        var context = session.ActiveContext;
        session.ActivatePlan(
            null,
            projectItems,
            new CraftSessionActiveContext(
                context.Region ?? "North America",
                context.DataCenter ?? "Aether",
                string.IsNullOrWhiteSpace(context.World) ? null : context.World,
                context.MarketFetchScope ?? MarketFetchScope.SelectedDataCenter),
            reason,
            CraftSessionIdentity.CreateNew(draftName));
    }

    private static string ResolveDraftName(CraftSessionState session)
    {
        if (!string.IsNullOrWhiteSpace(session.ActivePlan?.Name))
        {
            return session.ActivePlan.Name;
        }

        if (!string.IsNullOrWhiteSpace(session.Identity.Name) && session.Identity.Name != "Untitled Plan")
        {
            return session.Identity.Name;
        }

        return "Desktop Draft";
    }

    private static ProjectItem CreateProjectItem(
        DesktopKnownProjectItem definition,
        int? quantityOverride = null,
        bool? mustBeHqOverride = null)
        => new()
        {
            Id = definition.ItemId,
            Name = definition.Name,
            Quantity = quantityOverride ?? definition.Quantity,
            MustBeHq = mustBeHqOverride ?? definition.MustBeHq
        };

    private static ProjectItem CreateCustomProjectItem(int itemId, string name, int quantity, bool mustBeHq) =>
        new()
        {
            Id = itemId,
            Name = name,
            Quantity = quantity,
            MustBeHq = mustBeHq
        };

    private static ProjectItem CloneProjectItem(ProjectItem item) =>
        new()
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };
}

public sealed record DesktopPlanMutationResult(bool Changed, string Message);

public sealed record DesktopKnownProjectItem(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq);
