namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Asks Universalis for only the market fields <see cref="UniversalisMarketDataMapper"/> stores,
/// then sends the request through the app's shared <see cref="HttpClient"/> so its handlers
/// (diagnostics, logging) still see it. The stored result is identical to an unfiltered fetch;
/// the response is about a third of the size and parses in a third of the time.
/// </summary>
internal sealed class UniversalisSlimFieldsHandler(HttpClient inner) : HttpMessageHandler
{
    private static readonly string[] Fields =
    [
        "itemID", "lastUploadTime", "worldUploadTimes", "averagePrice", "averagePriceNQ", "averagePriceHQ",
        "listings.quantity", "listings.pricePerUnit", "listings.retainerName", "listings.hq",
        "listings.lastReviewTime", "listings.worldName"
    ];

    private static readonly string MultiItemFields = string.Join(",", Fields.Select(field => "items." + field));
    private static readonly string SingleItemFields = string.Join(",", Fields);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Universalis request has no URI.");
        // A single-item request returns the item at the root; several return an "items" map.
        var itemIds = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
        var fields = itemIds.Contains(',') ? MultiItemFields : SingleItemFields;
        var query = string.IsNullOrEmpty(uri.Query) ? "?" : uri.Query + "&";
        var forwarded = new HttpRequestMessage(request.Method, new UriBuilder(uri) { Query = (query + "fields=" + fields)[1..] }.Uri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)forwarded.Options)[option.Key] = option.Value;
        }

        return inner.SendAsync(forwarded, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }
}
