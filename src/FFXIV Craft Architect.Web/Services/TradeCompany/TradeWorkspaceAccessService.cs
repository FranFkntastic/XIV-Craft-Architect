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
    // A settled answer is cached until invalidated. Answers guessed while the host was
    // unreachable are not: the next call asks again, and the guess keeps the last known
    // answer so the tabs never vanish on a blip.
    private bool? _settled;
    private bool? _lastKnown;
    private Task<bool>? _inflight;
    private int _generation;

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

    /// <summary>The last known answer, or null before anything has resolved.</summary>
    public bool? CanOperate => _settled ?? _lastKnown;

    /// <summary>Whether <see cref="CanOperate"/> is a confirmed answer rather than a fallback.</summary>
    public bool IsSettled => _settled.HasValue;

    public Task<bool> CanOperateAsync()
    {
        if (_settled is { } settled)
        {
            return Task.FromResult(settled);
        }
        return _inflight ??= ResolveOnceAsync();
    }

    /// <summary>Forgets every answer, e.g. after the connected account changes.</summary>
    public void Invalidate()
    {
        _generation++;
        _settled = null;
        _lastKnown = null;
        _inflight = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Asks again on the next call (after a role-changing action) while the last known
    /// answer stays on screen.
    /// </summary>
    public void Refresh()
    {
        _generation++;
        _settled = null;
        _inflight = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies the host's role answer that arrived with fresh hub data, so promotions and
    /// demotions take effect without a reload.
    /// </summary>
    public async Task ObserveHostAnswerAsync(bool hostCanOperate)
    {
        var generation = _generation;
        var value = hostCanOperate || await HasLocalOnlyCompanyAsync();
        if (generation == _generation)
        {
            Settle(value);
        }
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

    private async Task<bool> ResolveOnceAsync()
    {
        var generation = _generation;
        try
        {
            var (value, settled) = await ResolveAsync();
            if (generation != _generation)
            {
                return value;
            }
            if (settled)
            {
                Settle(value);
            }
            else
            {
                Remember(value);
            }
            return value;
        }
        finally
        {
            if (generation == _generation)
            {
                _inflight = null;
            }
        }
    }

    private void Settle(bool value)
    {
        var changed = CanOperate != value || !IsSettled;
        _settled = value;
        _lastKnown = value;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private void Remember(bool value)
    {
        var changed = CanOperate != value;
        _lastKnown = value;
        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private async Task<(bool Value, bool Settled)> ResolveAsync()
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
            return (_lastKnown ?? true, false);
        }
        if (local.Any(profile => profile.SyncState == TradeSyncState.LocalOnly))
        {
            return (true, true);
        }

        var connection = await _localState.LoadConnectionSettingsAsync();
        if (!connection.IsConfigured)
        {
            return (local.Count > 0, true);
        }
        try
        {
            var hosted = await _hubs.LoadCrafterHubAccessAsync();
            if (hosted.HasValue)
            {
                return (hosted.Value, true);
            }
            Console.Error.WriteLine("[TradeAccess] Host could not resolve company roles; keeping the last known answer until the next navigation.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[TradeAccess] Host role lookup failed; keeping the last known answer until the next navigation. {exception.GetType().Name}: {exception.Message}");
        }
        return (_lastKnown ?? local.Count > 0, false);
    }

    private async Task<bool> HasLocalOnlyCompanyAsync()
    {
        try
        {
            return (await _persistence.LoadCompanyProfilesAsync())
                .Any(profile => profile.SyncState == TradeSyncState.LocalOnly);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[TradeAccess] Local company profiles unavailable while applying hub roles. {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    public void Dispose() => _sync.ConnectionChanged -= Invalidate;
}
