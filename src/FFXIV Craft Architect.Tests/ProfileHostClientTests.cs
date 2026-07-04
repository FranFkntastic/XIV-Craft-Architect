using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Tests;

public sealed class ProfileHostClientTests
{
    [Fact]
    public async Task GetHealthAsync_CallsProfileHostHealth()
    {
        var handler = new RecordingHandler("""{"service":"FFXIV Craft Architect Private Backend","status":"ready","profileHostEnabled":true}""");
        var client = new ProfileHostClient(new HttpClient(handler) { BaseAddress = new Uri("https://host.test/") });

        var health = await client.GetHealthAsync(CancellationToken.None);

        Assert.True(health.ProfileHostEnabled);
        Assert.Equal("/profile-host/health", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(string json)
        {
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_json, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
