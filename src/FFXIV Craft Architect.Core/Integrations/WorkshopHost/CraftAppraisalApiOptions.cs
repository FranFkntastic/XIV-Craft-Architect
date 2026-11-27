using Microsoft.Extensions.Configuration;

namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

/// <summary>
/// Settings for hosted craft appraisal. Build them from configuration with
/// <see cref="FromConfiguration"/>, or in code with an object initializer; both paths
/// share the defaults and the limits applied by <see cref="Normalize"/>.
/// </summary>
public sealed record CraftAppraisalApiOptions
{
    /// <summary>The route the Craft Architect hosted service serves plan snapshots from.</summary>
    public const string DefaultPlanSnapshotPath = "/craft/plans";

    public const string DefaultCacheDirectoryName = "craft-appraisal-cache";
    public const string DefaultPlanDirectoryName = "craft-appraisal-plans";

    private static readonly TimeSpan MinimumQuoteTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumQuoteTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan MinimumQuoteCacheLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumQuoteCacheLifetime = TimeSpan.FromSeconds(1800);

    public bool Enabled { get; init; }

    /// <summary>Market cache directory. Empty means <c>{contentRoot}/craft-appraisal-cache</c>.</summary>
    public string CacheDirectory { get; init; } = string.Empty;

    /// <summary>Plan snapshot directory. Empty means <c>{contentRoot}/craft-appraisal-plans</c>.</summary>
    public string PlanDirectory { get; init; } = string.Empty;

    /// <summary>
    /// Origin of the Craft Architect app that opens plan links. A quote's plan link is
    /// <c>{PublicAppOrigin}/?appraisalPlan={escaped snapshot URL}</c>.
    /// </summary>
    public string PublicAppOrigin { get; init; } = string.Empty;

    /// <summary>
    /// Base URL of the host that serves plan snapshots. It may include a path prefix.
    /// A plan's snapshot URL is <c>{PublicApiOrigin}{PlanSnapshotPath}/{planId}</c>.
    /// </summary>
    public string PublicApiOrigin { get; init; } = string.Empty;

    /// <summary>
    /// Route, relative to <see cref="PublicApiOrigin"/>, under which the host serves
    /// <c>{planId}</c> snapshots. Empty means snapshots are served directly under the origin.
    /// </summary>
    public string PlanSnapshotPath { get; init; } = DefaultPlanSnapshotPath;

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
                ?? Path.Combine(root, DefaultCacheDirectoryName)),
            PlanDirectory = Path.GetFullPath(
                configuration["CraftAppraisal:PlanDirectory"]
                ?? Path.Combine(root, DefaultPlanDirectoryName)),
            PublicAppOrigin = configuration["CraftAppraisal:PublicAppOrigin"] ?? string.Empty,
            PublicApiOrigin = configuration["CraftAppraisal:PublicApiOrigin"] ?? string.Empty,
            MaximumQuantity = configuration.GetValue("CraftAppraisal:MaximumQuantity", 999),
            MaximumConcurrentQuotes = configuration.GetValue("CraftAppraisal:MaximumConcurrentQuotes", 2),
            QuoteTimeout = TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue("CraftAppraisal:QuoteTimeoutSeconds", 45),
                5,
                180)),
            QuoteCacheLifetime = TimeSpan.FromSeconds(Math.Clamp(
                configuration.GetValue("CraftAppraisal:QuoteCacheSeconds", 300),
                30,
                1800)),
        }.Normalize(root);
    }

    /// <summary>
    /// Returns a copy with defaults filled in and every value brought inside its supported
    /// range: empty directories resolve under <paramref name="contentRoot"/>, origins and the
    /// snapshot path lose surrounding whitespace and trailing slashes, and quantity,
    /// concurrency, timeout and cache lifetime are clamped. Applying it twice changes nothing.
    /// </summary>
    public CraftAppraisalApiOptions Normalize(string contentRoot)
    {
        var root = Path.GetFullPath(contentRoot);
        return this with
        {
            CacheDirectory = ResolveDirectory(CacheDirectory, root, DefaultCacheDirectoryName),
            PlanDirectory = ResolveDirectory(PlanDirectory, root, DefaultPlanDirectoryName),
            PublicAppOrigin = NormalizeOrigin(PublicAppOrigin),
            PublicApiOrigin = NormalizeOrigin(PublicApiOrigin),
            PlanSnapshotPath = NormalizePath(PlanSnapshotPath),
            MaximumQuantity = Math.Clamp(MaximumQuantity, 1, 9999),
            MaximumConcurrentQuotes = Math.Clamp(MaximumConcurrentQuotes, 1, 8),
            QuoteTimeout = Clamp(QuoteTimeout, MinimumQuoteTimeout, MaximumQuoteTimeout),
            QuoteCacheLifetime = Clamp(QuoteCacheLifetime, MinimumQuoteCacheLifetime, MaximumQuoteCacheLifetime),
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

    /// <summary>The URL at which the host serves the snapshot for <paramref name="planId"/>.</summary>
    public string BuildPlanSnapshotUrl(string planId) =>
        $"{PublicApiOrigin}{PlanSnapshotPath}/{planId}";

    /// <summary>The Craft Architect app link that opens <paramref name="snapshotUrl"/>.</summary>
    public string BuildPlanUrl(string snapshotUrl) =>
        $"{PublicAppOrigin}/?appraisalPlan={Uri.EscapeDataString(snapshotUrl)}";

    private static string ResolveDirectory(string? value, string root, string defaultName) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? Path.Combine(root, defaultName) : value);

    private static string NormalizeOrigin(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');

    private static string NormalizePath(string? value)
    {
        var path = value?.Trim().Trim('/') ?? string.Empty;
        return path.Length == 0 ? string.Empty : "/" + path;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;
}
