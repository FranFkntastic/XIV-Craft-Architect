using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileHostClientTests
{
    [Fact]
    public async Task GetHealthAsync_CallsProfileHostHealth()
    {
        var handler = new RecordingHandler("""{"service":"FFXIV Craft Architect Private Backend","status":"ready","profileHostEnabled":true}""");
        var client = new ProfileHostClient(new HttpClient(handler) { BaseAddress = new Uri("https://host.test/") });

        var health = await client.GetHealthAsync("https://remote.test/craft/", CancellationToken.None);

        Assert.True(health.ProfileHostEnabled);
        Assert.Equal("remote.test", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal("/craft/profile-host/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PutObjectAsync_ReturnsConflictBody()
    {
        var handler = new RecordingHandler(
            """{"success":false,"conflict":true,"remoteObject":{"collection":"plans","objectId":"plan-1","payloadJson":"{}","revision":12,"updatedAtUtc":"2026-07-04T12:00:00Z","deleted":false}}""",
            System.Net.HttpStatusCode.Conflict);
        var client = new ProfileHostClient(new HttpClient(handler) { BaseAddress = new Uri("https://host.test/") });

        var result = await client.PutObjectAsync(
            "https://host.test/",
            "profile-key",
            "plans",
            "plan-1",
            new() { PayloadJson = "{}", ExpectedRevision = 10 },
            CancellationToken.None);

        Assert.True(result.Conflict);
        Assert.Equal(12, result.RemoteObject!.Revision);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly System.Net.HttpStatusCode _statusCode;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(string json, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
        {
            _json = json;
            _statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
