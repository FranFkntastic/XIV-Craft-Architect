using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
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
builder.Services.AddHttpClient<IGarlandService, GarlandService>();
builder.Services.AddSingleton<IXivItemDataProvider, GarlandXivItemDataProvider>();
builder.Services.AddSingleton(_ => new ProfileHostOptions
{
    DatabasePath = builder.Configuration["ProfileHost:DatabasePath"]
        ?? Path.Combine(AppContext.BaseDirectory, "profile-host.db")
});
builder.Services.AddSingleton<ProfileAccessKeyHasher>();
builder.Services.AddSingleton<SqliteProfileHostStore>();

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

app.MapGet(
    "/xivdata/items/search",
    async (
        string? q,
        int? limit,
        IXivItemDataProvider itemData,
        CancellationToken cancellationToken) =>
    {
        var query = q?.Trim() ?? string.Empty;
        if (query.Length == 0 || (query.Length == 1 && !char.IsDigit(query[0])))
        {
            return Results.BadRequest(new XivDataErrorResponse(
                "invalid_query",
                "Query must contain at least two characters unless it is an item ID."));
        }

        var clampedLimit = Math.Clamp(limit ?? 20, 1, 50);
        try
        {
            var items = await itemData.SearchAsync(query, clampedLimit, cancellationToken);
            return Results.Ok(new XivItemSearchResponse(items));
        }
        catch (HttpRequestException)
        {
            return Results.Json(
                new XivDataErrorResponse("upstream_unavailable", "The Garland item data source is unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            return Results.Json(
                new XivDataErrorResponse("upstream_invalid_response", "The Garland item data source returned an unexpected response."),
                statusCode: StatusCodes.Status502BadGateway);
        }
    });

app.MapGet(
    "/xivdata/items/{itemId:int}",
    async (
        int itemId,
        IXivItemDataProvider itemData,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var item = await itemData.GetItemAsync(itemId, cancellationToken);
            return item == null
                ? Results.NotFound(new XivDataErrorResponse("item_not_found", "Item was not found."))
                : Results.Ok(item);
        }
        catch (HttpRequestException)
        {
            return Results.Json(
                new XivDataErrorResponse("upstream_unavailable", "The Garland item data source is unavailable."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception)
        {
            return Results.Json(
                new XivDataErrorResponse("upstream_invalid_response", "The Garland item data source returned an unexpected response."),
            statusCode: StatusCodes.Status502BadGateway);
        }
    });

app.MapProfileHostEndpoints();

app.Run();

public partial class Program;
