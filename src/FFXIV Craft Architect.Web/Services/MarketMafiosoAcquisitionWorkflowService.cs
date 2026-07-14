using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketMafiosoAcquisitionWorkflowService
{
    private readonly IWorkshopHostAcquisitionClient _client;
    private readonly ISettingsService _settings;

    public MarketMafiosoAcquisitionWorkflowService(
        IWorkshopHostAcquisitionClient client,
        ISettingsService settings)
    {
        _client = client;
        _settings = settings;
    }

    public async Task<MarketMafiosoHandoffConfiguration> GetConfigurationAsync()
    {
        return new MarketMafiosoHandoffConfiguration(
            await _settings.GetAsync("marketmafioso.workshop_host_url", "https://dev.xivcraftarchitect.com/marketmafioso/api/") ?? string.Empty,
            await _settings.GetAsync("marketmafioso.api_key", string.Empty) ?? string.Empty,
            await _settings.GetAsync("marketmafioso.target_character", string.Empty) ?? string.Empty,
            await _settings.GetAsync("marketmafioso.target_world", string.Empty) ?? string.Empty);
    }

    public async Task<WorkshopHostAcquisitionRequestView> CreateSingleWorldHandoffAsync(
        MarketMafiosoSingleWorldHandoff handoff,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync();
        Validate(configuration, handoff);
        var connection = new WorkshopHostConnectionOptions
        {
            ApiBaseUrl = configuration.ApiUrl,
            ApiKey = configuration.ApiKey,
        };
        var capabilities = await _client.GetCapabilitiesAsync(connection, cancellationToken);
        if (!capabilities.Supports("acquisition.queue"))
            throw new InvalidOperationException("Workshop Host doesn't advertise the acquisition.queue v1 capability.");

        return await _client.CreateBatchAsync(
            connection,
            new WorkshopHostAcquisitionBatchCreateRequest
            {
                IdempotencyKey = $"ca-{Guid.NewGuid():N}",
                TargetCharacterName = configuration.TargetCharacter,
                TargetWorld = configuration.TargetWorld,
                Region = handoff.Region,
                WorldMode = "Selected",
                SelectedWorlds = [handoff.PurchaseWorld],
                ExpiresInSeconds = 3600,
                Lines =
                [
                    new WorkshopHostAcquisitionBatchLineCreateRequest
                    {
                        ItemId = checked((uint)handoff.ItemId),
                        ItemName = handoff.ItemName,
                        ItemKind = "Material",
                        TargetQuantity = handoff.Quantity,
                        MaxQuantity = handoff.Quantity,
                        MaxUnitPrice = handoff.MaxUnitPrice,
                        GilCap = handoff.GilCap,
                    },
                ],
            },
            cancellationToken);
    }

    public async Task<WorkshopHostAcquisitionTimeline> GetTimelineAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync();
        if (string.IsNullOrWhiteSpace(configuration.ApiUrl) || string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new InvalidOperationException("Configure the Workshop Host URL and API key in Options first.");

        return await _client.GetTimelineAsync(
            new WorkshopHostConnectionOptions
            {
                ApiBaseUrl = configuration.ApiUrl,
                ApiKey = configuration.ApiKey,
            },
            requestId,
            cancellationToken);
    }

    public static string DescribeNextEvidenceStep(string? status, string worldName)
    {
        var normalized = status?.Trim() ?? string.Empty;
        if (normalized.Equals("PendingPickup", StringComparison.OrdinalIgnoreCase))
            return "Open Market Acquisition in MarketMafioso, check the dashboard, and claim the request.";
        if (normalized.Equals("Claimed", StringComparison.OrdinalIgnoreCase))
            return "Accept the claimed request in MarketMafioso.";
        if (normalized.Equals("AcceptedInPlugin", StringComparison.OrdinalIgnoreCase))
            return $"Choose Refresh Evidence in MarketMafioso to read {worldName} without purchasing.";
        if (normalized.Equals("Running", StringComparison.OrdinalIgnoreCase))
            return "MarketMafioso is still working; sync again after the evidence refresh finishes.";

        return $"No {worldName} observation is available yet; review the request in MarketMafioso.";
    }

    private static void Validate(
        MarketMafiosoHandoffConfiguration configuration,
        MarketMafiosoSingleWorldHandoff handoff)
    {
        if (string.IsNullOrWhiteSpace(configuration.ApiUrl) || string.IsNullOrWhiteSpace(configuration.ApiKey))
            throw new InvalidOperationException("Configure the Workshop Host URL and API key in Options first.");
        if (string.IsNullOrWhiteSpace(configuration.TargetCharacter) || string.IsNullOrWhiteSpace(configuration.TargetWorld))
            throw new InvalidOperationException("Configure the MarketMafioso character and home world in Options first.");
        if (handoff.Quantity == 0 || handoff.MaxUnitPrice == 0 || handoff.GilCap == 0)
            throw new InvalidOperationException("Quantity, maximum unit price, and gil cap must all be greater than zero.");
    }
}

public sealed record MarketMafiosoHandoffConfiguration(
    string ApiUrl,
    string ApiKey,
    string TargetCharacter,
    string TargetWorld);

public sealed record MarketMafiosoSingleWorldHandoff(
    int ItemId,
    string ItemName,
    string Region,
    string DataCenter,
    string PurchaseWorld,
    uint Quantity,
    uint MaxUnitPrice,
    uint GilCap);
