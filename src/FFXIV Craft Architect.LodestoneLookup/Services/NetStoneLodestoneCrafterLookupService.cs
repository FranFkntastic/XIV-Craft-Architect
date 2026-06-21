using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using NetStone;
using NetStone.Search.Character;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services;

public sealed class NetStoneLodestoneCrafterLookupService : ILodestoneCrafterLookupService
{
    private const string LodestoneProfileBaseUrl = "https://na.finalfantasyxiv.com/lodestone/character/";
    private readonly ILogger<NetStoneLodestoneCrafterLookupService> _logger;
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private LodestoneClient? _client;

    public NetStoneLodestoneCrafterLookupService(ILogger<NetStoneLodestoneCrafterLookupService> logger)
    {
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

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = await GetClientAsync(cancellationToken);
            var candidates = new List<LodestoneCrafterSearchCandidate>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var dataCenter in GetSearchDataCenters(request))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await client.SearchCharacter(new CharacterSearchQuery
                {
                    CharacterName = request.CharacterName.Trim(),
                    World = request.WorldName?.Trim() ?? string.Empty,
                    DataCenter = string.IsNullOrWhiteSpace(request.WorldName)
                        ? dataCenter ?? string.Empty
                        : string.Empty
                });

                if (page?.Results == null)
                {
                    continue;
                }

                foreach (var result in page.Results.Where(result => !string.IsNullOrWhiteSpace(result.Id)))
                {
                    if (!seenIds.Add(result.Id!))
                    {
                        continue;
                    }

                    candidates.Add(new LodestoneCrafterSearchCandidate(
                        result.Id!,
                        result.Name,
                        request.WorldName,
                        string.IsNullOrWhiteSpace(request.WorldName) ? dataCenter : request.DataCenter,
                        $"{LodestoneProfileBaseUrl}{result.Id}/"));

                    if (candidates.Count >= 10)
                    {
                        return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Success(candidates);
                    }
                }
            }

            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Success(candidates);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lodestone character search failed for {CharacterName}", request.CharacterName);
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.NetworkUnavailable,
                "Lodestone could not be reached.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Lodestone character search failure");
            return LodestoneCrafterLookupResult<IReadOnlyList<LodestoneCrafterSearchCandidate>>.Failure(
                LodestoneCrafterLookupFailureKind.Unknown,
                ex.Message);
        }
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

        try
        {
            var normalizedCharacterId = lodestoneCharacterId.Trim();
            cancellationToken.ThrowIfCancellationRequested();
            var client = await GetClientAsync(cancellationToken);
            var character = await client.GetCharacter(normalizedCharacterId);
            if (character == null)
            {
                return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                    LodestoneCrafterLookupFailureKind.NotFound,
                    "Lodestone character was not found.");
            }

            var jobs = await client.GetCharacterClassJob(normalizedCharacterId);
            if (jobs == null)
            {
                return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                    LodestoneCrafterLookupFailureKind.ParseFailed,
                    "Lodestone class/job data could not be loaded.");
            }

            var preview = new LodestoneCrafterImportPreview(
                normalizedCharacterId,
                character.Name,
                character.Server,
                null,
                $"{LodestoneProfileBaseUrl}{normalizedCharacterId}/",
                character.Avatar?.ToString(),
                character.Portrait?.ToString(),
                character.FreeCompany?.Name,
                character.Race?.ToString(),
                character.Tribe?.ToString(),
                FormatGender(character.Gender),
                DateTime.UtcNow,
                [
                    new TradeCraftingJobLevel(TradeCraftingJob.Carpenter, jobs.Carpenter.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Blacksmith, jobs.Blacksmith.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Armorer, jobs.Armorer.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Goldsmith, jobs.Goldsmith.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Leatherworker, jobs.Leatherworker.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Weaver, jobs.Weaver.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Alchemist, jobs.Alchemist.Level),
                    new TradeCraftingJobLevel(TradeCraftingJob.Culinarian, jobs.Culinarian.Level)
                ]);

            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Success(preview);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Lodestone import preview failed for {LodestoneCharacterId}", lodestoneCharacterId);
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.NetworkUnavailable,
                "Lodestone could not be reached.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected Lodestone import preview failure");
            return LodestoneCrafterLookupResult<LodestoneCrafterImportPreview>.Failure(
                LodestoneCrafterLookupFailureKind.Unknown,
                ex.Message);
        }
    }

    private async Task<LodestoneClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            return _client;
        }

        await _clientLock.WaitAsync(cancellationToken);
        try
        {
            _client ??= await LodestoneClient.GetClientAsync();
            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private static IReadOnlyList<string?> GetSearchDataCenters(LodestoneCrafterSearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.WorldName))
        {
            return [null];
        }

        if (!string.IsNullOrWhiteSpace(request.DataCenter))
        {
            return [request.DataCenter.Trim()];
        }

        if (!string.IsNullOrWhiteSpace(request.Region))
        {
            var regionDataCenters = MarketFetchScopeResolver.GetDataCenters(
                    MarketFetchScope.EntireRegion,
                    string.Empty,
                    request.Region.Trim())
                .Where(dataCenter => !string.IsNullOrWhiteSpace(dataCenter))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(dataCenter => (string?)dataCenter)
                .ToArray();

            if (regionDataCenters.Length > 0)
            {
                return regionDataCenters;
            }
        }

        return [null];
    }

    private static string? FormatGender(char gender)
    {
        return gender switch
        {
            '♂' => "Male",
            '♀' => "Female",
            _ => gender == default ? null : gender.ToString()
        };
    }
}
