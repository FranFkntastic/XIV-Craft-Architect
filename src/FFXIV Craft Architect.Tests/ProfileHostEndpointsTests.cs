using System.Net;
using System.Net.Http.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileHostEndpointsTests
{
    [Fact]
    public async Task Health_ReturnsProfileHostEnabled()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var health = await client.GetFromJsonAsync<ProfileHostHealthResponse>("/profile-host/health");

        Assert.NotNull(health);
        Assert.Equal("ready", health.Status);
        Assert.True(health.ProfileHostEnabled);
    }

    [Fact]
    public async Task Profile_WithoutKey_ReturnsUnauthorized()
    {
        await using var app = new WebApplicationFactory<Program>();
        var client = app.CreateClient();

        var response = await client.GetAsync("/profile-host/profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutObject_WithValidKey_PersistsObjectForChanges()
    {
        using var temp = new TemporaryProfileHostDatabase();
        var key = await temp.CreateProfileAndKeyAsync();
        await using var app = temp.CreateFactory();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Profile-Key", key);

        var put = await client.PutAsJsonAsync(
            "/profile-host/objects/tradeOrders/order-1",
            new ProfileSyncPutRequest
            {
                PayloadJson = "{\"id\":\"order-1\"}",
                ExpectedRevision = 0
            });

        put.EnsureSuccessStatusCode();
        var putBody = await put.Content.ReadFromJsonAsync<ProfileSyncPutResponse>();
        var changes = await client.GetFromJsonAsync<ProfileSyncChangesResponse>("/profile-host/changes?sinceRevision=0");

        Assert.NotNull(putBody);
        Assert.True(putBody.Success);
        Assert.NotNull(changes);
        var saved = Assert.Single(changes.Objects);
        Assert.Equal(ProfileSyncCollections.TradeOrders, saved.Collection);
        Assert.Equal("order-1", saved.ObjectId);
        Assert.Equal("{\"id\":\"order-1\"}", saved.PayloadJson);
    }

    private sealed class TemporaryProfileHostDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        public async Task<string> CreateProfileAndKeyAsync()
        {
            var store = new SqliteProfileHostStore(new ProfileHostOptions { DatabasePath = _path });
            var hasher = new ProfileAccessKeyHasher();
            var key = hasher.CreateAccessKey();
            var profile = await store.CreateProfileAsync("Sapphire Avenue", CancellationToken.None);
            await store.AddAccessKeyAsync(profile.ProfileId, key.StoredHash, CancellationToken.None);
            return key.PlaintextKey;
        }

        public WebApplicationFactory<Program> CreateFactory()
        {
            return new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, configuration) =>
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ProfileHost:DatabasePath"] = _path
                        });
                    });
                });
        }

        public void Dispose()
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }
}
