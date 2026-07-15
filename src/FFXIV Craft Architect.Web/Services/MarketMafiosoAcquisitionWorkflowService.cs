using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services.Interfaces;

namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MarketMafiosoAcquisitionWorkflowService
{
    public const string PendingSubmissionSetting = "marketmafioso.pending_submission";

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
            await _settings.GetAsync("marketmafioso.workshop_host_url", string.Empty) ?? string.Empty,
            await _settings.GetAsync("marketmafioso.api_key", string.Empty) ?? string.Empty,
            await _settings.GetAsync("marketmafioso.target_character", string.Empty) ?? string.Empty,
            await _settings.GetAsync("marketmafioso.target_world", string.Empty) ?? string.Empty);
    }

    public async Task<MarketMafiosoConnectionTestResult> TestConnectionAsync(
        string apiUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Enter the Workshop Host API URL and client API key first.");
        }

        var capabilities = await _client.GetCapabilitiesAsync(
            new WorkshopHostConnectionOptions
            {
                ApiBaseUrl = apiUrl,
                ApiKey = apiKey,
            },
            cancellationToken);
        var acquisition = capabilities.Capabilities.FirstOrDefault(capability =>
            string.Equals(capability.Id, "acquisition.queue", StringComparison.OrdinalIgnoreCase));

        return new MarketMafiosoConnectionTestResult(
            capabilities.Service,
            capabilities.SchemaVersion,
            capabilities.Supports("acquisition.queue"),
            acquisition?.RequiredScopes ?? []);
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
        {
            throw new InvalidOperationException("Workshop Host doesn't advertise the acquisition.queue v1 capability.");
        }

        var pendingSubmission = await GetOrCreatePendingSubmissionAsync(configuration, handoff);

        var created = await _client.CreateBatchAsync(
            connection,
            new WorkshopHostAcquisitionBatchCreateRequest
            {
                IdempotencyKey = pendingSubmission.IdempotencyKey,
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
        try
        {
            await _settings.SetAsync(PendingSubmissionSetting, MarketMafiosoPendingSubmission.Empty);
        }
        catch
        {
            // The server response is authoritative. Retaining the submitted key is safer than
            // reporting a false failure and inviting a duplicate retry.
        }
        return created;
    }

    private async Task<MarketMafiosoPendingSubmission> GetOrCreatePendingSubmissionAsync(
        MarketMafiosoHandoffConfiguration configuration,
        MarketMafiosoSingleWorldHandoff handoff)
    {
        var expected = MarketMafiosoPendingSubmission.Create(configuration, handoff);
        var pending = await _settings.GetAsync(PendingSubmissionSetting, MarketMafiosoPendingSubmission.Empty)
            ?? MarketMafiosoPendingSubmission.Empty;
        if (pending.Matches(expected))
        {
            return pending;
        }

        await _settings.SetAsync(PendingSubmissionSetting, expected);
        return expected;
    }

    public async Task<WorkshopHostAcquisitionTimeline> GetTimelineAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        var configuration = await GetConfigurationAsync();
        if (string.IsNullOrWhiteSpace(configuration.ApiUrl) || string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new InvalidOperationException("Configure the Workshop Host URL and API key in Options first.");
        }

        return await _client.GetTimelineAsync(
            new WorkshopHostConnectionOptions
            {
                ApiBaseUrl = configuration.ApiUrl,
                ApiKey = configuration.ApiKey,
            },
            requestId,
            cancellationToken);
    }

    public async Task<MarketMafiosoEvidenceWaitResult> WaitForEvidenceAsync(
        string requestId,
        int itemId,
        string worldName,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default)
    {
        if (itemId <= 0 || string.IsNullOrWhiteSpace(worldName))
        {
            throw new ArgumentException("An item and purchase world are required to monitor MarketMafioso evidence.");
        }

        while (true)
        {
            var timeline = await GetTimelineAsync(requestId, cancellationToken);
            var observation = timeline.MarketObservations
                .Where(candidate =>
                    candidate.ItemId == itemId &&
                    candidate.WorldName.Equals(worldName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.ObservedAtUtc)
                .FirstOrDefault();
            if (observation != null)
            {
                return new MarketMafiosoEvidenceWaitResult(observation, timeline.Request.Status);
            }

            if (IsTerminalStatus(timeline.Request.Status))
            {
                return new MarketMafiosoEvidenceWaitResult(null, timeline.Request.Status);
            }

            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    public static string DescribeNextEvidenceStep(string? status, string worldName)
    {
        var normalized = status?.Trim() ?? string.Empty;
        if (normalized.Equals("PendingPickup", StringComparison.OrdinalIgnoreCase))
        {
            return "Open Market Acquisition in MarketMafioso, check the dashboard, and claim the request.";
        }

        if (normalized.Equals("Claimed", StringComparison.OrdinalIgnoreCase))
        {
            return "Accept the claimed request in MarketMafioso.";
        }

        if (normalized.Equals("AcceptedInPlugin", StringComparison.OrdinalIgnoreCase))
        {
            return $"Choose Refresh Evidence in MarketMafioso to read {worldName} without purchasing.";
        }

        if (normalized.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            return "MarketMafioso is still working; sync again after the evidence refresh finishes.";
        }

        if (normalized.Equals("RecoveryRequired", StringComparison.OrdinalIgnoreCase))
        {
            return "The MarketMafioso execution lease expired; resend it from Workshop Host after reviewing any reported purchases.";
        }

        return $"No {worldName} observation is available yet; review the request in MarketMafioso.";
    }

    private static void Validate(
        MarketMafiosoHandoffConfiguration configuration,
        MarketMafiosoSingleWorldHandoff handoff)
    {
        if (string.IsNullOrWhiteSpace(configuration.ApiUrl) || string.IsNullOrWhiteSpace(configuration.ApiKey))
        {
            throw new InvalidOperationException("Configure the Workshop Host URL and API key in Options first.");
        }

        if (string.IsNullOrWhiteSpace(configuration.TargetCharacter) || string.IsNullOrWhiteSpace(configuration.TargetWorld))
        {
            throw new InvalidOperationException("Configure the MarketMafioso character and home world in Options first.");
        }

        if (handoff.Quantity == 0 || handoff.MaxUnitPrice == 0 || handoff.GilCap == 0)
        {
            throw new InvalidOperationException("Quantity, maximum unit price, and gil cap must all be greater than zero.");
        }
    }

    public static bool IsTerminalStatus(string? status) =>
        status?.Trim().ToUpperInvariant() is "COMPLETED" or "FAILED" or "REJECTED" or "CANCELLED" or "EXPIRED" or "RECOVERYREQUIRED";
}

public sealed record MarketMafiosoHandoffConfiguration(
    string ApiUrl,
    string ApiKey,
    string TargetCharacter,
    string TargetWorld)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ApiUrl) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(TargetCharacter) &&
        !string.IsNullOrWhiteSpace(TargetWorld);
}

public sealed record MarketMafiosoConnectionTestResult(
    string Service,
    int SchemaVersion,
    bool AcquisitionQueueAvailable,
    IReadOnlyList<string> RequiredScopes);

public sealed record MarketMafiosoEvidenceWaitResult(
    WorkshopHostMarketObservation? Observation,
    string Status);

public sealed record MarketMafiosoSingleWorldHandoff(
    int ItemId,
    string ItemName,
    string Region,
    string DataCenter,
    string PurchaseWorld,
    uint Quantity,
    uint MaxUnitPrice,
    uint GilCap);

public sealed record MarketMafiosoPendingSubmission
{
    public static MarketMafiosoPendingSubmission Empty { get; } = new();

    public string IdempotencyKey { get; init; } = string.Empty;
    public string ApiUrl { get; init; } = string.Empty;
    public string TargetCharacter { get; init; } = string.Empty;
    public string TargetWorld { get; init; } = string.Empty;
    public int ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string DataCenter { get; init; } = string.Empty;
    public string PurchaseWorld { get; init; } = string.Empty;
    public uint Quantity { get; init; }
    public uint MaxUnitPrice { get; init; }
    public uint GilCap { get; init; }

    public static MarketMafiosoPendingSubmission Create(
        MarketMafiosoHandoffConfiguration configuration,
        MarketMafiosoSingleWorldHandoff handoff) =>
        new()
        {
            IdempotencyKey = $"ca-{Guid.NewGuid():N}",
            ApiUrl = configuration.ApiUrl.Trim().TrimEnd('/'),
            TargetCharacter = configuration.TargetCharacter.Trim(),
            TargetWorld = configuration.TargetWorld.Trim(),
            ItemId = handoff.ItemId,
            ItemName = handoff.ItemName.Trim(),
            Region = handoff.Region.Trim(),
            DataCenter = handoff.DataCenter.Trim(),
            PurchaseWorld = handoff.PurchaseWorld.Trim(),
            Quantity = handoff.Quantity,
            MaxUnitPrice = handoff.MaxUnitPrice,
            GilCap = handoff.GilCap,
        };

    public bool Matches(MarketMafiosoPendingSubmission other) =>
        !string.IsNullOrWhiteSpace(IdempotencyKey) &&
        ApiUrl.Equals(other.ApiUrl, StringComparison.OrdinalIgnoreCase) &&
        TargetCharacter.Equals(other.TargetCharacter, StringComparison.Ordinal) &&
        TargetWorld.Equals(other.TargetWorld, StringComparison.OrdinalIgnoreCase) &&
        ItemId == other.ItemId &&
        ItemName.Equals(other.ItemName, StringComparison.Ordinal) &&
        Region.Equals(other.Region, StringComparison.OrdinalIgnoreCase) &&
        DataCenter.Equals(other.DataCenter, StringComparison.OrdinalIgnoreCase) &&
        PurchaseWorld.Equals(other.PurchaseWorld, StringComparison.OrdinalIgnoreCase) &&
        Quantity == other.Quantity &&
        MaxUnitPrice == other.MaxUnitPrice &&
        GilCap == other.GilCap;
}

public sealed record MarketMafiosoActiveHandoffState
{
    public int StateVersion { get; init; }
    public string RequestId { get; init; } = string.Empty;
    public int ItemId { get; init; }
    public string DataCenter { get; init; } = string.Empty;
    public string PurchaseWorld { get; init; } = string.Empty;

    public bool IsActive =>
        StateVersion > 0 &&
        !string.IsNullOrWhiteSpace(RequestId) &&
        ItemId > 0 &&
        !string.IsNullOrWhiteSpace(DataCenter) &&
        !string.IsNullOrWhiteSpace(PurchaseWorld);
}
