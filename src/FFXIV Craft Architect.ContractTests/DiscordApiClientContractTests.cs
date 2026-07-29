using System.Net;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordApiClientContractTests
{
    [Fact]
    public async Task CreateMessage_AmbiguousFailureDoesNotBlindlyRetry()
    {
        var handler = new ScriptedHandler(_ => throw new HttpRequestException("uncertain"));
        var client = CreateClient(handler);

        var result = await client.CreateMessageAsync(
            "300000000000000003",
            DiscordCommissionMessage.CreateEphemeral("safe"));

        Assert.Equal(DiscordApiOutcome.ReconciliationRequired, result.Outcome);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Publication_FailsClosedForWrongChannelOrMissingMentionSuppression()
    {
        var handler = new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"300000000000000004"}""")
            });
        var client = CreateClient(handler);

        var wrongChannel = await client.CreateMessageAsync(
            "300000000000000099",
            DiscordCommissionMessage.CreateEphemeral("safe"));
        var unsafePayload = await client.CreateMessageAsync(
            "300000000000000003",
            new { content = "@everyone" });

        Assert.Equal(DiscordApiOutcome.TerminalFailure, wrongChannel.Outcome);
        Assert.Equal(DiscordApiOutcome.TerminalFailure, unsafePayload.Outcome);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AuthorizationFailure_IsTerminalAndBounded()
    {
        var handler = new ScriptedHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"missing access"}""")
            });
        var client = CreateClient(handler);

        var result = await client.EditMessageAsync(
            "300000000000000003",
            "300000000000000004",
            DiscordCommissionMessage.CreateEphemeral("safe"));

        Assert.Equal(DiscordApiOutcome.TerminalFailure, result.Outcome);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    private static DiscordApiClient CreateClient(HttpMessageHandler handler)
    {
        var options = new DiscordCommissionOptions
        {
            Enabled = true,
            ApplicationId = "300000000000000001",
            PublicKey = new string('0', 64),
            BotToken = "test-token",
            AllowedGuildId = "300000000000000002",
            AllowedChannelId = "300000000000000003",
            CommissionBaseUrl = "https://example.test/commission?id=",
            ApiBaseUrl = "https://discord.test/api/v10/"
        };
        return new DiscordApiClient(
            new HttpClient(handler) { BaseAddress = new Uri(options.ApiBaseUrl) },
            options,
            NullLogger<DiscordApiClient>.Instance);
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }
}
