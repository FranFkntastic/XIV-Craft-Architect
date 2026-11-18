using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

/// <summary>
/// Decides whether the operations tabs (Orders, Crafters, Margins) apply to this
/// account and where Trade Architect opens. Operating a hosted company, or keeping a
/// local company that was never hosted, makes the tabs apply; everyone else gets the
/// Crafter hub only.
/// </summary>
public sealed class TradeWorkspaceAccessService : IDisposable
{
    public const string StartWorkspaceKey = "ui.start_workspace";
    public const string TradeStartPageKey = "ui.trade_start_page";
    public const string HubRoute = "trade/hub";

    private static readonly IReadOnlyDictionary<string, string> StartPages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hub"] = HubRoute,
            ["orders"] = "trade/orders",
            ["crafters"] = "trade/crafters",
            ["margins"] = "trade/margins"
        };

    private readonly CompanyHubClient _hubs;
    private readonly ProfileSyncLocalStateService _localState;
    private readonly TradeOperationsPersistenceService _persistence;
    private readonly ProfileSyncService _sync;
    private readonly WebSettingsService _settings;
    private Task<bool>? _canOperate;

    public TradeWorkspaceAccessService(
        CompanyHubClient hubs,
        ProfileSyncLocalStateService localState,
        TradeOperationsPersistenceService persistence,
        ProfileSyncService sync,
        WebSettingsService settings)
    {
        _hubs = hubs;
        _localState = localState;
        _persistence = persistence;
        _sync = sync;
        _settings = settings;
        _sync.ConnectionChanged += Invalidate;
    }

    public event Action? Changed;

    /// <summary>The last resolved answer, or null before the first resolution finishes.</summary>
    public bool? CanOperate =>
        _canOperate is { IsCompletedSuccessfully: true } resolved ? resolved.Result : null;

    public Task<bool> CanOperateAsync() => _canOperate ??= ResolveAsync();

    public void Invalidate()
    {
        _canOperate = null;
        Changed?.Invoke();
    }

    public async Task<string> ResolveLandingAsync()
    {
        var preference = await _settings.GetAsync(TradeStartPageKey, "hub") ?? "hub";
        if (!StartPages.TryGetValue(preference, out var route) || route == HubRoute)
        {
            return HubRoute;
        }
        return await CanOperateAsync() ? route : HubRoute;
    }

    public async Task<bool> OpensToTradeAsync() =>
        string.Equals(
            await _settings.GetAsync(StartWorkspaceKey, "craft"),
            "trade",
            StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ResolveAsync()
    {
        IReadOnlyList<TradeCompanyProfile> local;
        try
        {
            local = await _persistence.LoadCompanyProfilesAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[TradeAccess] Local company profiles unavailable; keeping operations tabs. {exception.GetType().Name}: {exception.Message}");
            return true;
        }
        if (local.Any(profile => profile.SyncState == TradeSyncState.LocalOnly))
        {
            return true;
        }

        var connection = await _localState.LoadConnectionSettingsAsync();
        if (!connection.IsConfigured)
        {
            return local.Count > 0;
        }
        try
        {
            var hosted = await _hubs.LoadCrafterHubAccessAsync();
            if (hosted.HasValue)
            {
                return hosted.Value;
            }
            Console.Error.WriteLine("[TradeAccess] Host could not resolve company roles; falling back to local companies.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[TradeAccess] Host role lookup failed; falling back to local companies. {exception.GetType().Name}: {exception.Message}");
        }
        return local.Count > 0;
    }

    public void Dispose() => _sync.ConnectionChanged -= Invalidate;
}
