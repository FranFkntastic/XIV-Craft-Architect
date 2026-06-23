using System.Net.Http.Json;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed record LodestoneLookupClientOptions(Uri BaseAddress);

public sealed class HttpLodestoneCrafterLookupService : ILodestoneCrafterLookupService
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseAddress;
    private readonly ILogger<HttpLodestoneCrafterLookupService> _logger;

    public HttpLodestoneCrafterLookupService(
        HttpClient httpClient,
        LodestoneLookupClientOptions options,
        ILogger<HttpLodestoneCrafterLookupService> logger)
    {
        _httpClient = httpClient;
        _baseAddress = options.BaseAddress;
        _logger = logger;
    }

    public async Task<LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>> SearchAsync(
        LodestoneCrafterSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.CharacterName))
        {
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.InvalidRequest,
                "Character name is required.");
        }

        var query = BuildQuery(
            ("name", request.CharacterName),
            ("world", request.WorldName),
            ("dataCenter", request.DataCenter),
            ("region", request.Region));

        return await GetAsync<IReadOnlyList<LodestoneCrafterSearchCandidate>>(
            $"lodestone/crafters/search{query}",
            cancellationToken);
    }

    public async Task<LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>> GetImportPreviewAsync(
        string lodestoneCharacterId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lodestoneCharacterId))
        {
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.InvalidRequest,
                "Lodestone character id is required.");
        }

        return await GetAsync<LodestoneCrafterImportPreview>(
            $"lodestone/crafters/{Uri.EscapeDataString(lodestoneCharacterId.Trim())}/preview",
            cancellationToken);
    }

    private async Task<LodestoneCrafterLookupResult<T>> GetAsync<T>(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(_baseAddress, relativeUri);
        try
        {
            var response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return LodestoneCrafterLookupResult<T>.Failure(
                    LodestoneCrafterLookupFailureKind.NetworkUnavailable,
                    $"Lodestone lookup helper returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var result = await response.Content.ReadFromJsonAsync<LodestoneCrafterLookupResult<T>>(
                cancellationToken: cancellationToken);

            return result ?? LodestoneCrafterLookupResult<T>.Failure(
                LodestoneCrafterLookupFailureKind.ParseFailed,
                "Lodestone lookup helper returned an empty response.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Lodestone lookup helper returned a non-JSON response for {RequestUri}", requestUri);
            return LodestoneCrafterLookupResult<T>.Failure(
                LodestoneCrafterLookupFailureKind.ParseFailed,
                "Lodestone lookup helper returned a non-JSON response. " +
                $"Tried {requestUri}. " +
                "Check the configured Lodestone lookup base address and reverse proxy route.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lodestone lookup helper request failed for {RequestUri}", requestUri);
            return LodestoneCrafterLookupResult<T>.Failure(
                LodestoneCrafterLookupFailureKind.NetworkUnavailable,
                "Lodestone lookup helper request failed. " +
                $"Tried {requestUri}. " +
                "If this points at localhost, the browser is still using local-helper configuration. " +
                "If it points at this site, check the browser console/network details for the blocked request.");
        }
    }

    private static string BuildQuery(params (string Name, string? Value)[] values)
    {
        var parts = values
            .Where(value => !string.IsNullOrWhiteSpace(value.Value))
            .Select(value => $"{Uri.EscapeDataString(value.Name)}={Uri.EscapeDataString(value.Value!.Trim())}")
            .ToArray();

        return parts.Length == 0 ? string.Empty : $"?{string.Join("&", parts)}";
    }
}
