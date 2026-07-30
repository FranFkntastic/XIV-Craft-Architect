using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.LodestoneLookup.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
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
    Enabled = builder.Configuration.GetValue("ProfileHost:Enabled", false),
    DatabasePath = builder.Configuration["ProfileHost:DatabasePath"]
        ?? Path.Combine(AppContext.BaseDirectory, "profile-host.db")
});
builder.Services.AddSingleton<ProfileAccessKeyHasher>();
builder.Services.AddSingleton<ProfileAuthenticationGate>();
builder.Services.AddSingleton<SqliteProfileHostStore>();
var profileDatabasePath = builder.Configuration["ProfileHost:DatabasePath"]
    ?? Path.Combine(AppContext.BaseDirectory, "profile-host.db");
builder.Services.AddSingleton(_ => new CommissionBriefOptions
{
    Enabled = builder.Configuration.GetValue("CommissionBriefs:Enabled", true),
    DatabasePath = builder.Configuration["CommissionBriefs:DatabasePath"]
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profileDatabasePath))!, "commission-briefs.db"),
    PublicPageUrl = builder.Configuration["CommissionBriefs:PublicPageUrl"]
        ?? "http://localhost:5000/commission.html"
});
builder.Services.AddSingleton<SqliteCommissionBriefStore>();
builder.Services.AddSingleton(_ =>
{
    var discordDatabasePath = builder.Configuration["Discord:DatabasePath"]
        ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(profileDatabasePath))!,
            "discord-collaboration.db");
    return new DiscordCommissionOptions
    {
        Enabled = builder.Configuration.GetValue("Discord:Enabled", false),
        CompanyId = builder.Configuration["Discord:CompanyId"] ?? string.Empty,
        ApplicationId = builder.Configuration["Discord:ApplicationId"] ?? string.Empty,
        PublicKey = builder.Configuration["Discord:PublicKey"] ?? string.Empty,
        BotToken = builder.Configuration["Discord:RuntimeBotToken"] ?? string.Empty,
        AllowedGuildId = builder.Configuration["Discord:AllowedGuildId"] ?? string.Empty,
        AllowedChannelId = builder.Configuration["Discord:AllowedChannelId"] ?? string.Empty,
        CommissionBaseUrl = builder.Configuration["Discord:CommissionBaseUrl"]
            ?? "https://dev.xivcraftarchitect.com/commission.html?id=",
        ApiBaseUrl = builder.Configuration["Discord:ApiBaseUrl"]
            ?? "https://discord.com/api/v10/",
        DatabasePath = discordDatabasePath,
        OutboxMaximumAttempts = Math.Clamp(
            builder.Configuration.GetValue("Discord:OutboxMaximumAttempts", 5),
            1,
            10),
        OutboxLeaseDuration = TimeSpan.FromSeconds(Math.Clamp(
            builder.Configuration.GetValue("Discord:OutboxLeaseSeconds", 30),
            5,
            300)),
        OutboxPollInterval = TimeSpan.FromSeconds(Math.Clamp(
            builder.Configuration.GetValue("Discord:OutboxPollSeconds", 2),
            1,
            60))
    };
});
builder.Services.AddSingleton<ProfileHostedTradeCompanyService>();
builder.Services.AddSingleton<TradeCompanyAuthorization>();
builder.Services.AddSingleton<DiscordRequestVerifier>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<SqliteDiscordCollaborationStore>();
builder.Services.AddScoped<DiscordCompanyOrderAdapter>();
builder.Services.AddScoped<DiscordPublicationService>();
builder.Services.AddScoped<DiscordClaimService>();
builder.Services.AddHttpClient<IDiscordApiClient, DiscordApiClient>((services, client) =>
{
    var options = services.GetRequiredService<DiscordCommissionOptions>();
    client.BaseAddress = new Uri(
        options.ApiBaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? options.ApiBaseUrl
            : options.ApiBaseUrl + "/",
        UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHostedService<DiscordOutboxDispatcher>();

if (ProfileHostProvisioningCommand.TryParse(args) is { } profileHostCommand)
{
    var commandApp = builder.Build();
    await RunProfileHostProvisioningCommandAsync(profileHostCommand, commandApp.Services, CancellationToken.None);
    return;
}

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
app.MapCommissionBriefEndpoints();
app.MapCompanyCommissionBriefEndpoints();
app.MapDiscordCommissionEndpoints();
app.MapDiscordCollaborationEndpoints();

app.Run();

static async Task RunProfileHostProvisioningCommandAsync(
    ProfileHostProvisioningCommand command,
    IServiceProvider services,
    CancellationToken cancellationToken)
{
    using var scope = services.CreateScope();
    var store = scope.ServiceProvider.GetRequiredService<SqliteProfileHostStore>();
    var hasher = scope.ServiceProvider.GetRequiredService<ProfileAccessKeyHasher>();

    switch (command.Action)
    {
        case ProfileHostProvisioningAction.CreateProfile:
            {
                var displayName = command.DisplayName ?? throw new InvalidOperationException("Display name is required.");
                var profile = await store.CreateProfileAsync(displayName, cancellationToken);
                var key = hasher.CreateAccessKey();
                await store.AddAccessKeyAsync(profile.ProfileId, key.StoredHash, cancellationToken);
                WriteJson(new
                {
                    profile.ProfileId,
                    profile.DisplayName,
                    AccessKey = key.PlaintextKey
                });
                break;
            }
        case ProfileHostProvisioningAction.EnsureProfile:
            {
                var rawProfileId = command.ProfileId ??
                    throw new InvalidOperationException("Profile id is required.");
                if (!Guid.TryParseExact(rawProfileId, "D", out var parsedProfileId) ||
                    parsedProfileId == Guid.Empty)
                {
                    throw new InvalidOperationException(
                        "Profile id must be a non-empty UUID in canonical form.");
                }

                var displayName = command.DisplayName?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 120)
                {
                    throw new InvalidOperationException(
                        "Display name must contain 1 to 120 characters.");
                }

                var plaintextKey = Environment.GetEnvironmentVariable(
                    "CRAFT_ARCHITECT_PROFILE_ACCESS_KEY");
                if (string.IsNullOrWhiteSpace(plaintextKey) || plaintextKey.Length > 256)
                {
                    throw new InvalidOperationException(
                        "CRAFT_ARCHITECT_PROFILE_ACCESS_KEY must contain 1 to 256 characters.");
                }

                var ensured = await store.EnsureProfileAsync(
                    parsedProfileId.ToString("D"),
                    displayName,
                    plaintextKey,
                    hasher,
                    cancellationToken);
                WriteJson(new
                {
                    ensured.Profile.ProfileId,
                    ensured.Profile.DisplayName,
                    ensured.Created,
                    Ensured = true
                });
                break;
            }
        case ProfileHostProvisioningAction.RotateKey:
            {
                var profileId = command.ProfileId ?? throw new InvalidOperationException("Profile id is required.");
                var profile = await store.LoadProfileAsync(profileId, cancellationToken);
                if (profile == null)
                {
                    Environment.ExitCode = 1;
                    Console.Error.WriteLine($"Profile '{profileId}' was not found or is disabled.");
                    return;
                }

                await store.RevokeAccessKeysAsync(profileId, cancellationToken);
                var key = hasher.CreateAccessKey();
                await store.AddAccessKeyAsync(profileId, key.StoredHash, cancellationToken);
                WriteJson(new
                {
                    profile.ProfileId,
                    profile.DisplayName,
                    AccessKey = key.PlaintextKey
                });
                break;
            }
        case ProfileHostProvisioningAction.DisableProfile:
            {
                var profileId = command.ProfileId ?? throw new InvalidOperationException("Profile id is required.");
                await store.RevokeAccessKeysAsync(profileId, cancellationToken);
                await store.DisableProfileAsync(profileId, cancellationToken);
                WriteJson(new { ProfileId = profileId, Disabled = true });
                break;
            }
        case ProfileHostProvisioningAction.ExportProfile:
            {
                var profileId = command.ProfileId ?? throw new InvalidOperationException("Profile id is required.");
                var profile = await store.LoadProfileAsync(profileId, cancellationToken);
                if (profile == null)
                {
                    Environment.ExitCode = 1;
                    Console.Error.WriteLine($"Profile '{profileId}' was not found or is disabled.");
                    return;
                }

                var changes = await store.LoadChangesAsync(profileId, 0, cancellationToken);
                WriteJson(new
                {
                    Profile = profile,
                    Objects = changes.Objects
                });
                break;
            }
        default:
            throw new InvalidOperationException($"Unsupported profile host command action '{command.Action}'.");
    }
}

static void WriteJson(object payload)
{
    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
}

public partial class Program;
