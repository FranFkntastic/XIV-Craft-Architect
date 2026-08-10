using System.Net;
using FFXIV_Craft_Architect.Web.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiagnosticRequestHandlerTests
{
    [Fact]
    public async Task RequestLog_IsBoundedAndNewestFirst()
    {
        var log = new ClientRequestLog(2);
        using var client = CreateClient(log, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        await client.GetAsync("https://profile.test/api/first");
        await client.GetAsync("https://profile.test/api/second");
        await client.GetAsync("https://profile.test/api/third");

        var entries = log.GetNewestFirst();
        Assert.Collection(
            entries,
            entry => Assert.Equal("https://profile.test/api/third", entry.Url),
            entry => Assert.Equal("https://profile.test/api/second", entry.Url));
    }

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

    [Fact]
    public async Task NetworkFailure_IsRecordedAndEnrichedWithMethodAndSanitizedUrl()
    {
        var log = new ClientRequestLog();
        using var client = CreateClient(
            log,
            new StubHandler(_ => throw new HttpRequestException("network unavailable")));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.DeleteAsync("https://profile.test/api/profile-host/profile?token=secret"));

        Assert.Contains("DELETE https://profile.test/api/profile-host/profile", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", exception.Message, StringComparison.Ordinal);
        var entry = Assert.Single(log.GetNewestFirst());
        Assert.Equal(nameof(HttpRequestException), entry.Result);
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
