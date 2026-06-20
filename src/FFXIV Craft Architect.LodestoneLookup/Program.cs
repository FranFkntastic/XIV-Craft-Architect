using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services;

const string CorsPolicyName = "CraftArchitectWeb";

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

var app = builder.Build();

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
        ILodestoneCrafterLookupService lookup,
        CancellationToken cancellationToken) =>
    {
        var result = await lookup.SearchAsync(
            new LodestoneCrafterSearchRequest(name, world, dataCenter),
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

app.Run();
