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
        {
            return Results.Json(
                new { error = CraftAppraisalRequestValidation.Unavailable },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var rejection = CraftAppraisalRequestValidation.GetRejection(request, options);
        if (rejection != null)
        {
            return Results.BadRequest(new { error = rejection });
        }

        try
        {
            return Results.Ok(await coordinator.AppraiseAsync(request, cancellationToken));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Results.Json(
                new { error = CraftAppraisalRequestValidation.Timeout },
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
            ? Results.NotFound(new { error = CraftAppraisalRequestValidation.PlanNotFound })
            : Results.Text(planJson, "application/json");
    }
}
