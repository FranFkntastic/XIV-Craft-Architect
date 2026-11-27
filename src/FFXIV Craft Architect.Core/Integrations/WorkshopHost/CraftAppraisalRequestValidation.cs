namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

/// <summary>
/// Request checks a host applies before handing a request to
/// <see cref="IHostedCraftAppraisalCoordinator"/>. Error codes are the ones the Craft
/// Architect hosted service returns in its <c>{"error": code}</c> bodies.
/// </summary>
public static class CraftAppraisalRequestValidation
{
    public const string Unavailable = "craft_appraisal_unavailable";
    public const string Timeout = "craft_appraisal_timeout";
    public const string PlanNotFound = "craft_appraisal_plan_not_found";
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string ItemIdRequired = "item_id_required";
    public const string QuantityRequired = "quantity_required";
    public const string QuantityExceedsLimit = "quantity_exceeds_limit";
    public const string UnsupportedPricingMode = "unsupported_pricing_mode";

    /// <summary>
    /// Returns the error code for the first rule <paramref name="request"/> breaks, or
    /// <see langword="null"/> when it can be appraised.
    /// </summary>
    public static string? GetRejection(
        CraftAppraisalRequest request,
        CraftAppraisalApiOptions options)
    {
        if (request.SchemaVersion != 1)
        {
            return UnsupportedSchemaVersion;
        }

        if (request.ItemId == 0)
        {
            return ItemIdRequired;
        }

        if (request.Quantity == 0)
        {
            return QuantityRequired;
        }

        if (request.Quantity > options.MaximumQuantity)
        {
            return QuantityExceedsLimit;
        }

        if (!request.Options.PricingMode.Equals(
                "CurrentMarketEvidence",
                StringComparison.OrdinalIgnoreCase))
        {
            return UnsupportedPricingMode;
        }

        return null;
    }
}
