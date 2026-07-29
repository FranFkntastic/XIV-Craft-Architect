using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Chaos.NaCl;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordCommissionContractTests
{
    [Fact]
    public async Task SignedPing_DoesNotRequireChannelProvisioning()
    {
        var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        Ed25519.KeyPairFromSeed(out var publicKey, out var expandedPrivateKey, seed);
        var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Discord:Enabled"] = "true",
                        ["Discord:PublicKey"] = Convert.ToHexString(publicKey)
                    });
                });
            });

        using var client = application.CreateClient();
        using var healthResponse = await client.GetAsync("/discord/health");
        using var response = await SendSignedAsync(
            client,
            JsonSerializer.SerializeToUtf8Bytes(new { type = 1 }),
            expandedPrivateKey);
        using var invalidJsonResponse = await SendSignedAsync(
            client,
            Encoding.UTF8.GetBytes("{"),
            expandedPrivateKey);
        using var oversizedResponse = await SendSignedAsync(
            client,
            new byte[(128 * 1024) + 1],
            expandedPrivateKey);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        using var health = JsonDocument.Parse(await healthResponse.Content.ReadAsByteArrayAsync());
        using var payload = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal("pending-channel", health.RootElement.GetProperty("status").GetString());
        Assert.True(health.RootElement.GetProperty("signingReady").GetBoolean());
        Assert.False(health.RootElement.GetProperty("publishingReady").GetBoolean());
        Assert.Equal(1, payload.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, invalidJsonResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

        await application.DisposeAsync();
    }

    [Fact]
    public async Task SignedCommand_ProjectsAuthoritativeBriefOnlyIntoConfiguredChannel()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"ca-discord-{Guid.NewGuid():N}.db");
        var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        Ed25519.KeyPairFromSeed(out var publicKey, out var expandedPrivateKey, seed);
        var application = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CommissionBriefs:Enabled"] = "true",
                        ["CommissionBriefs:DatabasePath"] = databasePath,
                        ["Discord:Enabled"] = "true",
                        ["Discord:PublicKey"] = Convert.ToHexString(publicKey),
                        ["Discord:AllowedGuildId"] = "guild-1",
                        ["Discord:AllowedChannelId"] = "channel-1",
                        ["Discord:CommissionBaseUrl"] = "https://dev.xivcraftarchitect.com/commission.html?id="
                    });
                });
            });

        using var client = application.CreateClient();
        var brief = new CommissionBriefDocument
        {
            CompanyName = "Sapphire Avenue Exchange",
            Title = "Shark-class Stern ×40",
            Reference = "CA-260729-DISCORD",
            Contact = "franfkntastic",
            Outputs = [new CommissionBriefOutput(21792, "Shark-class Stern", 40, true)],
            CrafterMaterials =
            [
                new CommissionBriefMaterial(
                    10371,
                    "Cobalt Ingot",
                    78,
                    false,
                    79_194,
                    6_177_132)
            ],
            Payment = new CommissionBriefPayment(
                "Labor standard",
                6_177_174,
                617_717,
                1_752_000,
                8_546_891,
                10,
                2_920,
                600),
            Evidence = new CommissionBriefEvidence(
                "Selected acquisition sources",
                "North America",
                "Aether",
                DateTime.UtcNow)
        };
        using var createResponse = await client.PostAsJsonAsync(
            "/xivdata/commission-briefs",
            new CommissionBriefCreateRequest { Brief = brief });
        createResponse.EnsureSuccessStatusCode();
        var created = (await createResponse.Content.ReadFromJsonAsync<CommissionBriefCreateResponse>())!;

        using var pingResponse = await SendSignedAsync(
            client,
            JsonSerializer.SerializeToUtf8Bytes(new { type = 1 }),
            expandedPrivateKey);
        using var commandResponse = await SendSignedAsync(
            client,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = 2,
                guild_id = "guild-1",
                channel_id = "channel-1",
                data = new
                {
                    name = "commission",
                    options = new[]
                    {
                        new
                        {
                            name = "post",
                            options = new[]
                            {
                                new
                                {
                                    name = "brief",
                                    type = 3,
                                    value = $"https://dev.xivcraftarchitect.com/commission.html?id={created.PublicId}"
                                }
                            }
                        }
                    }
                }
            }),
            expandedPrivateKey);
        using var wrongChannelResponse = await SendSignedAsync(
            client,
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = 2,
                guild_id = "guild-1",
                channel_id = "elsewhere",
                data = new
                {
                    name = "commission",
                    options = Array.Empty<object>()
                }
            }),
            expandedPrivateKey);
        using var invalidSignature = new HttpRequestMessage(HttpMethod.Post, "/discord/interactions")
        {
            Content = JsonContent.Create(new { type = 1 })
        };
        invalidSignature.Headers.Add("X-Signature-Timestamp", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        invalidSignature.Headers.Add("X-Signature-Ed25519", new string('0', 128));
        using var invalidSignatureResponse = await client.SendAsync(invalidSignature);

        using var ping = JsonDocument.Parse(await pingResponse.Content.ReadAsByteArrayAsync());
        using var command = JsonDocument.Parse(await commandResponse.Content.ReadAsByteArrayAsync());
        using var wrongChannel = JsonDocument.Parse(await wrongChannelResponse.Content.ReadAsByteArrayAsync());
        var commandData = command.RootElement.GetProperty("data");
        var embed = commandData.GetProperty("embeds")[0];

        Assert.Equal(1, ping.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(4, command.RootElement.GetProperty("type").GetInt32());
        Assert.Equal(brief.Title, embed.GetProperty("title").GetString());
        Assert.Contains("8,546,891 gil total", embed.ToString(), StringComparison.Ordinal);
        Assert.Contains("franfkntastic", embed.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(
            created.PublicId,
            commandData.GetProperty("components")[0]
                .GetProperty("components")[0]
                .GetProperty("url")
                .GetString(),
            StringComparison.Ordinal);
        Assert.Empty(commandData.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Equal(64, wrongChannel.RootElement.GetProperty("data").GetProperty("flags").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, invalidSignatureResponse.StatusCode);

        await application.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    private static async Task<HttpResponseMessage> SendSignedAsync(
        HttpClient client,
        byte[] body,
        byte[] expandedPrivateKey)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var timestampBytes = Encoding.ASCII.GetBytes(timestamp);
        var signedBody = new byte[timestampBytes.Length + body.Length];
        timestampBytes.CopyTo(signedBody, 0);
        body.CopyTo(signedBody, timestampBytes.Length);
        var signature = Ed25519.Sign(signedBody, expandedPrivateKey);
        var request = new HttpRequestMessage(HttpMethod.Post, "/discord/interactions")
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Signature-Timestamp", timestamp);
        request.Headers.Add("X-Signature-Ed25519", Convert.ToHexString(signature));
        return await client.SendAsync(request);
    }
}
