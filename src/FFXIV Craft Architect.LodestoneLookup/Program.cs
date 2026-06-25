using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.XivData;

const string CorsPolicyName = "CraftArchitectWeb";
const string PrivateNetworkAccessRequestHeader = "Access-Control-Request-Private-Network";
const string PrivateNetworkAccessResponseHeader = "Access-Control-Allow-Private-Network";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5000",
                "http://localhost:5001",
                "https://localhost:5001",
                "https://franfkntastic.github.io")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddSingleton<ILodestoneCrafterLookupService, NetStoneLodestoneCrafterLookupService>();
builder.Services.AddHttpClient<GarlandService>();
builder.Services.AddSingleton<IGarlandService>(sp => sp.GetRequiredService<GarlandService>());
builder.Services.AddSingleton<IXivItemDataProvider, GarlandXivItemDataProvider>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Headers.ContainsKey("Origin") ||
        context.Request.Headers.ContainsKey(PrivateNetworkAccessRequestHeader))
    {
        context.Response.Headers[PrivateNetworkAccessResponseHeader] = "true";
    }

    await next();
});

app.UseCors(CorsPolicyName);

app.MapGet("/", () => Results.Ok(new
{
    service = "FFXIV Craft Architect Lodestone Lookup",
    status = "ready"
}));

app.MapGet(
    "/lodestone/crafters/search",
    async (
        string name,
        string? world,
        string? dataCenter,
        string? region,
        ILodestoneCrafterLookupService lookup,
        CancellationToken cancellationToken) =>
    {
        var result = await lookup.SearchAsync(
            new LodestoneCrafterSearchRequest(name, world, dataCenter, region),
            cancellationToken);
        return Results.Ok(result);
    });

app.MapGet(
    "/lodestone/crafters/{characterId}/preview",
    async (
        string characterId,
        ILodestoneCrafterLookupService lookup,
        CancellationToken cancellationToken) =>
    {
        var result = await lookup.GetImportPreviewAsync(characterId, cancellationToken);
        return Results.Ok(result);
    });

Program.MapXivDataRoutes(app);

app.Run();

public partial class Program
{
    public static void MapXivDataRoutes(WebApplication app)
    {
        app.MapGet(
            "/xivdata/items/search",
            async (
                string? q,
                int? limit,
                IXivItemDataProvider provider,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(q) ||
                    (q.Trim().Length < 2 && !uint.TryParse(q.Trim(), out _)))
                {
                    return Error(
                        StatusCodes.Status400BadRequest,
                        "invalid_query",
                        "Search query must be at least two characters, unless it is a numeric item id.");
                }

                try
                {
                    var results = await provider.SearchItemsAsync(
                        q.Trim(),
                        Math.Clamp(limit ?? 20, 1, 50),
                        cancellationToken);
                    return Results.Ok(new XivItemSearchResponse
                    {
                        Query = q.Trim(),
                        Items = results,
                    });
                }
                catch (HttpRequestException)
                {
                    return Error(
                        StatusCodes.Status503ServiceUnavailable,
                        "provider_unavailable",
                        "The XIV item data provider is unavailable.");
                }
                catch (ArgumentException ex)
                {
                    return Error(
                        StatusCodes.Status400BadRequest,
                        "invalid_query",
                        ex.Message);
                }
                catch (Exception)
                {
                    return Error(
                        StatusCodes.Status502BadGateway,
                        "provider_bad_response",
                        "The XIV item data provider returned invalid data.");
                }
            });

        app.MapGet(
            "/xivdata/items/{itemId:int}",
            async (
                int itemId,
                IXivItemDataProvider provider,
                CancellationToken cancellationToken) =>
            {
                if (itemId <= 0)
                {
                    return Error(
                        StatusCodes.Status400BadRequest,
                        "invalid_item_id",
                        "Item id must be a positive integer.");
                }

                try
                {
                    var item = await provider.GetItemAsync((uint)itemId, cancellationToken);
                    return item == null
                        ? Error(
                            StatusCodes.Status404NotFound,
                            "item_not_found",
                            $"Item {itemId} was not found.")
                        : Results.Ok(item);
                }
                catch (HttpRequestException)
                {
                    return Error(
                        StatusCodes.Status503ServiceUnavailable,
                        "provider_unavailable",
                        "The XIV item data provider is unavailable.");
                }
                catch (Exception)
                {
                    return Error(
                        StatusCodes.Status502BadGateway,
                        "provider_bad_response",
                        "The XIV item data provider returned invalid data.");
                }
            });
    }

    private static IResult Error(int statusCode, string errorCode, string message) =>
        Results.Json(
            new XivDataErrorResponse
            {
                ErrorCode = errorCode,
                Message = message,
            },
            statusCode: statusCode);
}
