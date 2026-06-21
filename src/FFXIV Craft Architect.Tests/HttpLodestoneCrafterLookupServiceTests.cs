using System.Net;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.Tests;

public sealed class HttpLodestoneCrafterLookupServiceTests
{
    [Fact]
    public async Task SearchAsync_SendsEncodedSearchRequestAndReturnsCandidates()
    {
        HttpRequestMessage? capturedRequest = null;
        var candidate = new LodestoneCrafterSearchCandidate(
            "16331040",
            "Level Checker",
            "Behemoth",
            "Primal",
            "https://na.finalfantasyxiv.com/lodestone/character/16331040/");
        var response = LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Success(
            new[] { candidate });
        var service = CreateService(request =>
        {
            capturedRequest = request;
            return JsonResponse(response);
        });

        var result = await service.SearchAsync(new LodestoneCrafterSearchRequest(
            "Level Checker",
            "Behemoth",
            null));

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value);
        Assert.Equal("Level Checker", result.Value[0].DisplayName);
        Assert.NotNull(capturedRequest?.RequestUri);
        Assert.Equal(
            "/lodestone/crafters/search?name=Level%20Checker&world=Behemoth",
            capturedRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task SearchAsync_WhenRegionProvided_SendsRegionSearchRequest()
    {
        HttpRequestMessage? capturedRequest = null;
        var response = LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Success([]);
        var service = CreateService(request =>
        {
            capturedRequest = request;
            return JsonResponse(response);
        });

        var result = await service.SearchAsync(new LodestoneCrafterSearchRequest(
            "Level Checker",
            null,
            null,
            "North America"));

        Assert.True(result.Succeeded);
        Assert.NotNull(capturedRequest?.RequestUri);
        Assert.Equal(
            "/lodestone/crafters/search?name=Level%20Checker&region=North%20America",
            capturedRequest!.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task GetImportPreviewAsync_WhenHelperUnavailable_ReturnsNetworkFailure()
    {
        var service = CreateService(_ => throw new HttpRequestException("No connection could be made."));

        var result = await service.GetImportPreviewAsync("16331040");

        Assert.False(result.Succeeded);
        Assert.Equal(LodestoneCrafterLookupFailureKind.NetworkUnavailable, result.FailureKind);
        Assert.Contains("Lodestone lookup helper is unavailable", result.ErrorMessage);
        Assert.Contains("Apps on device", result.ErrorMessage);
    }

    private static HttpLodestoneCrafterLookupService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(handler))
        {
            BaseAddress = new Uri("http://localhost:5128/")
        };
        return new HttpLodestoneCrafterLookupService(
            httpClient,
            new LodestoneLookupClientOptions(new Uri("http://localhost:5128/")),
            NullLogger<HttpLodestoneCrafterLookupService>.Instance);
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
