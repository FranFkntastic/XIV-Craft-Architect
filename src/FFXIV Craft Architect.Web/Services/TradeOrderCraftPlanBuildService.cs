using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record TradeRequestedCraftOutput(
    int ItemId,
    string Name,
    int Quantity,
    bool MustBeHq);

public sealed record TradeOrderCraftPlanBuildRequest(
    IReadOnlyList<TradeRequestedCraftOutput> Outputs,
    string DataCenter,
    string World);

public sealed record TradeOrderCraftPlanBuildResult(
    bool Built,
    CraftingPlan? Plan,
    IReadOnlyList<MaterialAggregate> ActiveProcurementItems,
    string? UnavailableReason)
{
    public static TradeOrderCraftPlanBuildResult Unavailable(string reason)
    {
        return new TradeOrderCraftPlanBuildResult(false, null, [], reason);
    }

    public static TradeOrderCraftPlanBuildResult Available(
        CraftingPlan plan,
        IReadOnlyList<MaterialAggregate> activeProcurementItems)
    {
        return new TradeOrderCraftPlanBuildResult(true, plan, activeProcurementItems, null);
    }
}

public sealed class TradeOrderCraftPlanBuildService
{
    private readonly IRecipePlanBuilder _recipePlanBuilder;
    private readonly IRecipeLayerWorkflowService _recipeLayerWorkflow;

    public TradeOrderCraftPlanBuildService(
        IRecipePlanBuilder recipePlanBuilder,
        IRecipeLayerWorkflowService recipeLayerWorkflow)
    {
        _recipePlanBuilder = recipePlanBuilder ?? throw new ArgumentNullException(nameof(recipePlanBuilder));
        _recipeLayerWorkflow = recipeLayerWorkflow ?? throw new ArgumentNullException(nameof(recipeLayerWorkflow));
    }

    public Task<TradeOrderCraftPlanBuildResult> BuildFromOrderAsync(
        TradeOrder order,
        string dataCenter,
        string world,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var snapshot = order.SourceSnapshot;
        if (snapshot?.RootItems == null)
        {
            return Task.FromResult(TradeOrderCraftPlanBuildResult.Unavailable(
                "This order does not have requested outputs to rebuild from."));
        }

        return BuildAsync(
            new TradeOrderCraftPlanBuildRequest(
                snapshot.RootItems
                    .Select(item => new TradeRequestedCraftOutput(
                        item.ItemId,
                        item.Name,
                        item.Quantity,
                        item.MustBeHq))
                    .ToArray(),
                dataCenter,
                world),
            ct);
    }

    public async Task<TradeOrderCraftPlanBuildResult> BuildAsync(
        TradeOrderCraftPlanBuildRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Outputs == null)
        {
            return TradeOrderCraftPlanBuildResult.Unavailable("Add at least one requested output before building the order craft plan.");
        }

        var targetItems = request.Outputs
            .Where(output => output.Quantity > 0)
            .Select(output => (output.ItemId, output.Name, output.Quantity, output.MustBeHq))
            .ToList();
        if (targetItems.Count == 0)
        {
            return TradeOrderCraftPlanBuildResult.Unavailable("Add at least one requested output before building the order craft plan.");
        }

        var plan = await _recipePlanBuilder.BuildPlanAsync(
            targetItems,
            request.DataCenter,
            request.World,
            ct);
        await _recipePlanBuilder.FetchVendorPricesAsync(plan, ct);
        var activeProcurementItems = _recipeLayerWorkflow.BuildActiveProcurementItems(plan);
        return TradeOrderCraftPlanBuildResult.Available(plan, activeProcurementItems);
    }
}
