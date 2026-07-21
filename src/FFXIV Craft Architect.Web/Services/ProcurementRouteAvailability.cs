namespace FFXIV_Craft_Architect.Web.Services;

public sealed record ProcurementRouteAvailability(bool IsGenerationEnabled)
{
    public const string DisabledMessage =
        "Procurement route generation is temporarily unavailable while CA resolves a performance issue. Market Analysis and manual acquisition choices remain available.";
}
