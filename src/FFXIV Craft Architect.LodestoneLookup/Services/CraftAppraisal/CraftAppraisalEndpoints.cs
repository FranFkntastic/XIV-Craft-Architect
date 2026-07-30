using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CraftAppraisal;

public static class CraftAppraisalEndpoints
{
    public static RouteGroupBuilder MapCraftAppraisalEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/craft");
        group.MapGet("/capabilities", (IHostedCraftAppraisalCoordinator coordinator) =>
            Results.Ok(new
            {
                service = "CraftArchitect",
                schemaVersion = 1,
                capabilities = coordinator.IsAvailable
                    ? new[]
                    {
                        new
                        {
                            id = "craft.appraise",
                            status = "available",
                            supportedSchemaVersions = new[] { 1 },
                        },
                    }
                    : [],
            }));
        group.MapPost("/appraise", AppraiseAsync);
        group.MapGet("/plans/{planId}", OpenPlanAsync);
        return group;
    }

    private static async Task<IResult> AppraiseAsync(
        CraftAppraisalRequest request,
        CraftAppraisalApiOptions options,
        IHostedCraftAppraisalCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!coordinator.IsAvailable)
            return Results.Json(
                new { error = "craft_appraisal_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        if (request.SchemaVersion != 1)
            return Results.BadRequest(new { error = "unsupported_schema_version" });
        if (request.ItemId == 0)
            return Results.BadRequest(new { error = "item_id_required" });
        if (request.Quantity == 0)
            return Results.BadRequest(new { error = "quantity_required" });
        if (request.Quantity > options.MaximumQuantity)
            return Results.BadRequest(new { error = "quantity_exceeds_limit" });
        if (!request.Options.PricingMode.Equals(
                "CurrentMarketEvidence",
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = "unsupported_pricing_mode" });
        }

        try
        {
            return Results.Ok(await coordinator.AppraiseAsync(request, cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { error = "craft_appraisal_timeout" },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }

    private static async Task<IResult> OpenPlanAsync(
        string planId,
        CraftAppraisalPlanStore planStore,
        CancellationToken cancellationToken)
    {
        var planJson = await planStore.ReadAsync(planId, cancellationToken);
        return planJson == null
            ? Results.NotFound(new { error = "craft_appraisal_plan_not_found" })
            : Results.Text(planJson, "application/json");
    }
}
