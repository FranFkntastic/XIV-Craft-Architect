using System.Net;
using FFXIV_Craft_Architect.Web.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiagnosticRequestHandlerTests
{
    [Fact]
    public async Task RequestLog_StripsQueryAndNeverRecordsHeaderMaterial()
    {
        const string profileKey = "cap_super-secret-key";
        var log = new ClientRequestLog();
        using var client = CreateClient(log, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://profile.test/api/profile-host/changes?sinceRevision=12&secret=query-value");
        request.Headers.Add("X-Profile-Key", profileKey);

        await client.SendAsync(request);

        var entry = Assert.Single(log.GetNewestFirst());
        Assert.Equal("https://profile.test/api/profile-host/changes", entry.Url);
        var recorded = string.Join('|', entry.TimestampUtc, entry.Method, entry.Url, entry.Result, entry.DurationMilliseconds);
        Assert.DoesNotContain(profileKey, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain("query-value", recorded, StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(ClientRequestLog log, HttpMessageHandler innerHandler) =>
        new(new DiagnosticRequestHandler(log, NullLogger<DiagnosticRequestHandler>.Instance)
        {
            InnerHandler = innerHandler
        });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
