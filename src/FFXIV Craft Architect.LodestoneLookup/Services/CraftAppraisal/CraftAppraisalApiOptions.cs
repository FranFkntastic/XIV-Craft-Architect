namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CraftAppraisal;

public sealed record CraftAppraisalApiOptions
{
    public bool Enabled { get; init; }
    public string CacheDirectory { get; init; } = string.Empty;
    public string PlanDirectory { get; init; } = string.Empty;
    public string PublicAppOrigin { get; init; } = string.Empty;
    public string PublicApiOrigin { get; init; } = string.Empty;
    public int MaximumQuantity { get; init; } = 999;
    public int MaximumConcurrentQuotes { get; init; } = 2;
    public TimeSpan QuoteTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan QuoteCacheLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public static CraftAppraisalApiOptions FromConfiguration(
        IConfiguration configuration,
        string contentRoot)
    {
        var root = Path.GetFullPath(contentRoot);
        return new CraftAppraisalApiOptions
        {
            Enabled = configuration.GetValue("CraftAppraisal:Enabled", false),
            CacheDirectory = Path.GetFullPath(
                configuration["CraftAppraisal:CacheDirectory"]
                ?? Path.Combine(root, "craft-appraisal-cache")),
            PlanDirectory = Path.GetFullPath(
                configuration["CraftAppraisal:PlanDirectory"]
                ?? Path.Combine(root, "craft-appraisal-plans")),
            PublicAppOrigin = NormalizeOrigin(
                configuration["CraftAppraisal:PublicAppOrigin"]),
            PublicApiOrigin = NormalizeOrigin(
                configuration["CraftAppraisal:PublicApiOrigin"]),
            MaximumQuantity = Math.Clamp(
                configuration.GetValue("CraftAppraisal:MaximumQuantity", 999),
                1,
                9999),
            MaximumConcurrentQuotes = Math.Clamp(
                configuration.GetValue("CraftAppraisal:MaximumConcurrentQuotes", 2),
                1,
                8),
            QuoteTimeout = TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue("CraftAppraisal:QuoteTimeoutSeconds", 45),
                5,
                180)),
            QuoteCacheLifetime = TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue("CraftAppraisal:QuoteCacheSeconds", 300),
                30,
                1800)),
        };
    }

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(PublicAppOrigin))
        {
            throw new InvalidOperationException("CraftAppraisal:PublicAppOrigin is required when craft appraisal is enabled.");
        }

        if (string.IsNullOrWhiteSpace(PublicApiOrigin))
        {
            throw new InvalidOperationException("CraftAppraisal:PublicApiOrigin is required when craft appraisal is enabled.");
        }
    }

    private static string NormalizeOrigin(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
}
