using System.Text.Json;
using System.Runtime.CompilerServices;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Core.Services;

public interface ICraftSessionDispatcher
{
    void Dispatch(Action action);
}

public sealed class ImmediateCraftSessionDispatcher : ICraftSessionDispatcher
{
    public void Dispatch(Action action) => action();
}

public sealed class CraftSessionState
{
    private readonly ICraftSessionDispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly HashSet<CraftSessionDirtyBucket> _dirtyBuckets = new();
    private readonly List<CraftSessionChange> _changes = new();
    private readonly List<CraftSessionChange> _deferredChanges = new();
    private CraftingPlan? _activePlan;
    private ProjectItem[] _projectItems = [];
    private CraftSessionActiveContext _activeContext = new(null, null, null, null);
    private CraftSessionMarketEvidence _marketEvidence = CraftSessionMarketEvidence.Empty;
    private CraftSessionProcurementOverlay? _procurementOverlay;
    private CraftSessionViewState _viewState = new();
    private readonly CraftSessionVersions _versions = new();
    private readonly ConditionalWeakTable<CraftingPlan, PlanSessionToken> _planSessionTokens = new();
    private long _planSessionVersion;
    private int _publicationDepth;

    public CraftSessionState(ICraftSessionDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public event EventHandler<CraftSessionChange>? Changed;

    public CraftSessionIdentity Identity { get; private set; } = CraftSessionIdentity.CreateNew();
    public long PlanSessionVersion
    {
        get
        {
            lock (_gate)
            {
                return _planSessionVersion;
            }
        }
    }

    public CraftSessionVersions Versions
    {
        get
        {
            lock (_gate)
            {
                return _versions.Clone();
            }
        }
    }

    public CraftingPlan? ActivePlan
    {
        get
        {
            lock (_gate)
            {
                var clone = ClonePlan(_activePlan);
                RegisterPlanInstance(clone, _planSessionVersion);
                return clone;
            }
        }
    }

    public CraftSessionActiveContext ActiveContext
    {
        get
        {
            lock (_gate)
            {
                return _activeContext;
            }
        }
    }

    public CraftSessionMarketEvidence MarketEvidence
    {
        get
        {
            lock (_gate)
            {
                return CloneMarketEvidence(_marketEvidence);
            }
        }
    }

    public CraftSessionProcurementOverlay? ProcurementOverlay
    {
        get
        {
            lock (_gate)
            {
                return CloneProcurementOverlay(_procurementOverlay);
            }
        }
    }

    public CraftSessionViewState ViewState
    {
        get
        {
            lock (_gate)
            {
                return _viewState.Clone();
            }
        }
    }

    public bool HasProcurementOverlay
    {
        get
        {
            lock (_gate)
            {
                return _procurementOverlay != null;
            }
        }
    }

    public IReadOnlyList<ProjectItem> ProjectItems
    {
        get
        {
            lock (_gate)
            {
                return _projectItems.Select(CloneProjectItem).ToArray();
            }
        }
    }

    public IReadOnlyCollection<CraftSessionDirtyBucket> DirtyBuckets
    {
        get
        {
            lock (_gate)
            {
                return _dirtyBuckets.ToArray();
            }
        }
    }

    public IReadOnlyList<CraftSessionChange> Changes
    {
        get
        {
            lock (_gate)
            {
                return _changes.ToArray();
            }
        }
    }

    public CraftSessionVersionStamp CaptureVersionStamp()
    {
        lock (_gate)
        {
            return _versions.Capture(_planSessionVersion);
        }
    }

    public bool IsCurrent(CraftSessionVersionStamp stamp)
    {
        lock (_gate)
        {
            return _versions.Capture(_planSessionVersion).Equals(stamp);
        }
    }

    public bool IsCurrentPlanSession(CraftingPlan? plan, long planSessionVersion)
    {
        lock (_gate)
        {
            return IsCurrentPlanSessionUnderLock(plan, planSessionVersion);
        }
    }

    public bool IsDirty(CraftSessionDirtyBucket bucket)
    {
        lock (_gate)
        {
            return _dirtyBuckets.Contains(bucket);
        }
    }

    public bool TryPublishFrom(CraftSessionVersionStamp expectedStamp, Action publish)
    {
        var published = TryPublishFrom(expectedStamp, publish, out var changesToDispatch);
        DispatchPublicationChanges(changesToDispatch);
        return published;
    }

    internal bool TryPublishFrom(
        CraftSessionVersionStamp expectedStamp,
        Action publish,
        out CraftSessionChange[] changesToDispatch)
    {
        lock (_gate)
        {
            if (!_versions.Capture(_planSessionVersion).Equals(expectedStamp))
            {
                changesToDispatch = [];
                return false;
            }

            var rollbackSnapshot = CaptureRollbackSnapshot();
            _publicationDepth++;
            try
            {
                publish();
            }
            catch
            {
                RestoreRollbackSnapshot(rollbackSnapshot);
                _deferredChanges.Clear();
                changesToDispatch = [];
                throw;
            }
            finally
            {
                _publicationDepth--;
            }

            if (_publicationDepth == 0 && _deferredChanges.Count > 0)
            {
                changesToDispatch = _deferredChanges.ToArray();
                _deferredChanges.Clear();
            }
            else
            {
                changesToDispatch = [];
            }
        }

        return true;
    }

    internal void DispatchPublicationChanges(IReadOnlyCollection<CraftSessionChange> changes) =>
        DispatchChanges(changes);

    public void ClearDirtyBucket(CraftSessionDirtyBucket bucket)
    {
        lock (_gate)
        {
            _dirtyBuckets.Remove(bucket);
        }
    }

    public void ClearDirtyBuckets(params CraftSessionDirtyBucket[] buckets)
    {
        if (buckets.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var bucket in buckets)
            {
                _dirtyBuckets.Remove(bucket);
            }
        }
    }

    public CraftSessionChange MarkPlanStructureChanged(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.PlanCore,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.PlanCore },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.PlanCore++;
            });

    public CraftSessionChange ActivatePlan(
        CraftingPlan? plan,
        IEnumerable<ProjectItem> projectItems,
        CraftSessionActiveContext activeContext,
        string reason,
        CraftSessionIdentity? identity = null)
    {
        lock (_gate)
        {
            if (identity != null)
            {
                Identity = identity;
            }

            _activePlan = ClonePlan(plan);
            _projectItems = projectItems.Select(CloneProjectItem).ToArray();
            _activeContext = activeContext;
            _marketEvidence = CraftSessionMarketEvidence.Empty;
            _planSessionVersion++;
        }

        var change = MarkPlanStructureChanged(reason);
        MarkMarketEvidenceChanged("market evidence cleared for active plan");
        lock (_gate)
        {
            RegisterPlanInstance(plan, _planSessionVersion);
            RegisterPlanInstance(_activePlan, _planSessionVersion);
        }

        return change;
    }

    public bool TryReplaceActivePlanPrices(
        CraftSessionVersionStamp expectedStamp,
        CraftingPlan plan,
        long planSessionVersion,
        IEnumerable<int> unavailableMarketItemIds,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(unavailableMarketItemIds);

        var applied = false;
        var published = TryPublishFrom(expectedStamp, () =>
        {
            if (!IsCurrentPlanSessionUnderLock(plan, planSessionVersion))
            {
                return;
            }

            _activePlan = ClonePlan(plan);
            _marketEvidence = new CraftSessionMarketEvidence(
                _marketEvidence.ItemAnalyses.Select(CloneMarketItemAnalysis).ToArray(),
                unavailableMarketItemIds.ToHashSet(),
                _marketEvidence.ShoppingPlans?.Select(CloneDetailedShoppingPlan).ToArray(),
                _marketEvidence.PublishedAgainstVersion);
            MarkPlanPriceChanged(reason);
            MarkMarketEvidenceChanged("unavailable market items updated");
            RegisterPlanInstance(plan, _planSessionVersion);
            RegisterPlanInstance(_activePlan, _planSessionVersion);
            applied = true;
        });

        return published && applied;
    }

    public bool TryReplaceActivePlanDecisions(
        CraftSessionVersionStamp expectedStamp,
        CraftingPlan plan,
        long planSessionVersion,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var applied = false;
        var published = TryPublishFrom(expectedStamp, () =>
        {
            if (!IsCurrentPlanSessionUnderLock(plan, planSessionVersion))
            {
                return;
            }

            _activePlan = ClonePlan(plan);
            MarkPlanDecisionChanged(reason);
            RegisterPlanInstance(plan, _planSessionVersion);
            RegisterPlanInstance(_activePlan, _planSessionVersion);
            applied = true;
        });

        return published && applied;
    }

    public CraftSessionChange MarkPlanDecisionChanged(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.PlanDecision,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.PlanCore },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.PlanDecision++;
            });

    public CraftSessionChange MarkPlanPriceChanged(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.PlanCore,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.PlanCore },
            reason,
            invalidatesProcurementOverlay: false,
            versions =>
            {
                versions.PlanPrice++;
            });

    public CraftSessionChange PublishMarketAnalysis(string reason) =>
        PublishMarketAnalysis(Array.Empty<MarketItemAnalysis>(), Array.Empty<int>(), reason);

    public CraftSessionChange PublishMarketAnalysis(
        IEnumerable<MarketItemAnalysis> itemAnalyses,
        IEnumerable<int> unavailableMarketItemIds,
        string reason)
    {
        return ApplyChange(
            CraftSessionChangeScope.MarketAnalysis,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.MarketAnalysis },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.MarketAnalysis++;
            },
            () =>
            {
                _marketEvidence = new CraftSessionMarketEvidence(
                    itemAnalyses.Select(CloneMarketItemAnalysis).ToArray(),
                    unavailableMarketItemIds.ToHashSet(),
                    Array.Empty<DetailedShoppingPlan>(),
                    _versions.Capture(_planSessionVersion));
            });
    }

    public CraftSessionChange ClearMarketAnalysis(string reason)
    {
        return ApplyChange(
            CraftSessionChangeScope.MarketAnalysis,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.MarketAnalysis },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.MarketAnalysis++;
            },
            () =>
            {
                _marketEvidence = CraftSessionMarketEvidence.Empty;
            });
    }

    public bool TryPublishMarketAnalysis(
        CraftSessionVersionStamp expectedStamp,
        CraftingPlan plan,
        long planSessionVersion,
        IEnumerable<MarketItemAnalysis> itemAnalyses,
        IEnumerable<DetailedShoppingPlan> shoppingPlans,
        bool acquisitionDecisionsChanged,
        string reason,
        IEnumerable<int>? unavailableMarketItemIds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(itemAnalyses);
        ArgumentNullException.ThrowIfNull(shoppingPlans);

        var published = false;
        var analysisList = itemAnalyses.Select(CloneMarketItemAnalysis).ToArray();
        var shoppingPlanList = shoppingPlans.Select(CloneDetailedShoppingPlan).ToArray();
        var unavailableIds = unavailableMarketItemIds?.ToHashSet() ?? new HashSet<int>();
        var completed = TryPublishFrom(expectedStamp, () =>
        {
            if (!IsCurrentPlanSessionUnderLock(plan, planSessionVersion))
            {
                return;
            }

            _activePlan = ClonePlan(plan);
            MarkMarketAnalysisPublished(reason);
            if (acquisitionDecisionsChanged)
            {
                MarkPlanDecisionChanged("market analysis reconciled acquisition decisions");
            }

            _marketEvidence = new CraftSessionMarketEvidence(
                analysisList.Select(CloneMarketItemAnalysis).ToArray(),
                unavailableIds.ToHashSet(),
                shoppingPlanList.Select(CloneDetailedShoppingPlan).ToArray(),
                _versions.Capture(_planSessionVersion));

            RegisterPlanInstance(plan, _planSessionVersion);
            RegisterPlanInstance(_activePlan, _planSessionVersion);
            published = true;
        });

        return completed && published;
    }

    private CraftSessionChange MarkMarketEvidenceChanged(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.MarketAnalysis,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.MarketAnalysis },
            reason,
            invalidatesProcurementOverlay: false,
            versions =>
            {
                versions.MarketAnalysis++;
            });

    private CraftSessionChange MarkMarketAnalysisPublished(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.MarketAnalysis,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.MarketAnalysis },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.MarketAnalysis++;
            });

    public CraftSessionChange PublishProcurementOverlay(CraftSessionProcurementOverlay overlay, string reason)
    {
        return MarkProcurementOverlayPublished(reason, () =>
        {
            _procurementOverlay = CloneProcurementOverlay(overlay);
        });
    }

    private CraftSessionChange MarkProcurementOverlayPublished(string reason, Action? mutateState = null) =>
        ApplyChange(
            CraftSessionChangeScope.ProcurementOverlay,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.Procurement },
            reason,
            invalidatesProcurementOverlay: false,
            versions =>
            {
                versions.Procurement++;
            },
            mutateState);

    public CraftSessionChange MarkProcurementSettingsChanged(string reason)
    {
        return ApplyChange(
            CraftSessionChangeScope.SettingsContext | CraftSessionChangeScope.MarketAnalysis,
            new HashSet<CraftSessionDirtyBucket>
            {
                CraftSessionDirtyBucket.SettingsContext,
                CraftSessionDirtyBucket.MarketAnalysis
            },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.SettingsContext++;
                versions.MarketAnalysis++;
            },
            () =>
            {
                _marketEvidence = CraftSessionMarketEvidence.Empty;
            });
    }

    public CraftSessionChange MarkMarketAnalysisSettingsChanged(string reason)
    {
        return ApplyChange(
            CraftSessionChangeScope.SettingsContext,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.SettingsContext },
            reason,
            invalidatesProcurementOverlay: true,
            versions =>
            {
                versions.SettingsContext++;
            });
    }

    public CraftSessionChange MarkViewStateChanged(string reason) =>
        ApplyChange(
            CraftSessionChangeScope.ViewState,
            new HashSet<CraftSessionDirtyBucket> { CraftSessionDirtyBucket.ViewState },
            reason,
            invalidatesProcurementOverlay: false,
            versions =>
            {
                versions.ViewState++;
            });

    public void ReplaceIdentity(CraftSessionIdentity identity)
    {
        lock (_gate)
        {
            Identity = identity ?? throw new ArgumentNullException(nameof(identity));
        }

        MarkPlanStructureChanged("session identity replaced");
    }

    private CraftSessionChange ApplyChange(
        CraftSessionChangeScope scope,
        IReadOnlySet<CraftSessionDirtyBucket> dirtyBuckets,
        string reason,
        bool invalidatesProcurementOverlay,
        Action<CraftSessionVersions> updateVersions,
        Action? mutateState = null)
    {
        CraftSessionChange change;
        bool deferDispatch;
        lock (_gate)
        {
            mutateState?.Invoke();
            updateVersions(_versions);
            var clearedProcurementOverlay = invalidatesProcurementOverlay && _procurementOverlay != null;
            if (invalidatesProcurementOverlay)
            {
                _procurementOverlay = null;
            }

            foreach (var bucket in dirtyBuckets)
            {
                _dirtyBuckets.Add(bucket);
            }

            if (clearedProcurementOverlay)
            {
                _dirtyBuckets.Add(CraftSessionDirtyBucket.Procurement);
            }

            var publishedDirtyBuckets = dirtyBuckets.ToHashSet();
            if (clearedProcurementOverlay)
            {
                publishedDirtyBuckets.Add(CraftSessionDirtyBucket.Procurement);
            }

            change = new CraftSessionChange(
                scope | (invalidatesProcurementOverlay ? CraftSessionChangeScope.ProcurementOverlay : CraftSessionChangeScope.None),
                publishedDirtyBuckets,
                invalidatesProcurementOverlay,
                reason);
            _changes.Add(change);
            if (_publicationDepth > 0)
            {
                _deferredChanges.Add(change);
                deferDispatch = true;
            }
            else
            {
                deferDispatch = false;
            }
        }

        if (!deferDispatch)
        {
            DispatchChanges([change]);
        }

        return change;
    }

    private void DispatchChanges(IReadOnlyCollection<CraftSessionChange> changes)
    {
        foreach (var change in changes)
        {
            _dispatcher.Dispatch(() => Changed?.Invoke(this, change));
        }
    }

    private static ProjectItem CloneProjectItem(ProjectItem item) =>
        new()
        {
            Id = item.Id,
            Name = item.Name,
            IconId = item.IconId,
            Quantity = item.Quantity,
            MustBeHq = item.MustBeHq
        };

    private static CraftingPlan? ClonePlan(CraftingPlan? plan)
    {
        if (plan == null)
        {
            return null;
        }

        var clone = new CraftingPlan
        {
            Id = plan.Id,
            Name = plan.Name,
            CreatedAt = plan.CreatedAt,
            ModifiedAt = plan.ModifiedAt,
            DataCenter = plan.DataCenter,
            World = plan.World,
            PriceVersion = plan.PriceVersion,
            RootItems = plan.RootItems.Select(node =>
            {
                var nodeClone = node.Clone();
                nodeClone.Parent = null;
                return nodeClone;
            }).ToList(),
            SavedMarketPlans = plan.SavedMarketPlans.Select(CloneDetailedShoppingPlan).ToList()
        };

        return clone;
    }

    private static CraftSessionMarketEvidence CloneMarketEvidence(CraftSessionMarketEvidence evidence) =>
        new(
            evidence.ItemAnalyses.Select(CloneMarketItemAnalysis).ToArray(),
            evidence.UnavailableMarketItemIds.ToHashSet(),
            evidence.ShoppingPlans?.Select(CloneDetailedShoppingPlan).ToArray(),
            evidence.PublishedAgainstVersion);

    private static MarketItemAnalysis CloneMarketItemAnalysis(MarketItemAnalysis analysis) =>
        JsonSerializer.Deserialize<MarketItemAnalysis>(JsonSerializer.Serialize(analysis)) ?? new MarketItemAnalysis();

    private static DetailedShoppingPlan CloneDetailedShoppingPlan(DetailedShoppingPlan plan) =>
        JsonSerializer.Deserialize<DetailedShoppingPlan>(JsonSerializer.Serialize(plan)) ?? new DetailedShoppingPlan();

    private static CraftSessionProcurementOverlay? CloneProcurementOverlay(CraftSessionProcurementOverlay? overlay) =>
        overlay == null
            ? null
            : new CraftSessionProcurementOverlay(
                overlay.PublishedAtUtc,
                overlay.ActiveItemIds.ToArray(),
                overlay.SourceDescription,
                overlay.ShoppingPlans?.Select(CloneDetailedShoppingPlan).ToArray());

    private void RegisterPlanInstance(CraftingPlan? plan, long planSessionVersion)
    {
        if (plan == null)
        {
            return;
        }

        _planSessionTokens.Remove(plan);
        _planSessionTokens.Add(plan, new PlanSessionToken(planSessionVersion, _versions.Capture(_planSessionVersion)));
    }

    private bool IsCurrentPlanSessionUnderLock(CraftingPlan? plan, long planSessionVersion)
    {
        if (_planSessionVersion != planSessionVersion)
        {
            return false;
        }

        return plan == null
            ? _activePlan == null
            : _activePlan != null &&
              _planSessionTokens.TryGetValue(plan, out var token) &&
              token.Version == _planSessionVersion &&
              token.Stamp.Equals(_versions.Capture(_planSessionVersion));
    }

    private CraftSessionRollbackSnapshot CaptureRollbackSnapshot() =>
        new(
            Identity,
            _planSessionVersion,
            ClonePlan(_activePlan),
            _projectItems.Select(CloneProjectItem).ToArray(),
            _activeContext,
            CloneMarketEvidence(_marketEvidence),
            CloneProcurementOverlay(_procurementOverlay),
            _viewState.Clone(),
            _versions.Capture(_planSessionVersion),
            _dirtyBuckets.ToHashSet(),
            _changes.Count,
            _deferredChanges.Count);

    private void RestoreRollbackSnapshot(CraftSessionRollbackSnapshot snapshot)
    {
            Identity = snapshot.Identity;
        _planSessionVersion = snapshot.PlanSessionVersion;
        _activePlan = ClonePlan(snapshot.ActivePlan);
        _projectItems = snapshot.ProjectItems.Select(CloneProjectItem).ToArray();
        _activeContext = snapshot.ActiveContext;
        _marketEvidence = CloneMarketEvidence(snapshot.MarketEvidence);
        _procurementOverlay = CloneProcurementOverlay(snapshot.ProcurementOverlay);
        _viewState = snapshot.ViewState.Clone();
        RestoreVersions(snapshot.Versions);

        _dirtyBuckets.Clear();
        foreach (var bucket in snapshot.DirtyBuckets)
        {
            _dirtyBuckets.Add(bucket);
        }

        if (_changes.Count > snapshot.ChangeCount)
        {
            _changes.RemoveRange(snapshot.ChangeCount, _changes.Count - snapshot.ChangeCount);
        }

        if (_deferredChanges.Count > snapshot.DeferredChangeCount)
        {
            _deferredChanges.RemoveRange(snapshot.DeferredChangeCount, _deferredChanges.Count - snapshot.DeferredChangeCount);
        }
    }

    private void RestoreVersions(CraftSessionVersionStamp versions)
    {
        _versions.PlanCore = versions.PlanCore;
        _versions.PlanDecision = versions.PlanDecision;
        _versions.PlanPrice = versions.PlanPrice;
        _versions.MarketAnalysis = versions.MarketAnalysis;
        _versions.Procurement = versions.Procurement;
        _versions.SettingsContext = versions.SettingsContext;
        _versions.ViewState = versions.ViewState;
    }

    private sealed record CraftSessionRollbackSnapshot(
        CraftSessionIdentity Identity,
        long PlanSessionVersion,
        CraftingPlan? ActivePlan,
        ProjectItem[] ProjectItems,
        CraftSessionActiveContext ActiveContext,
        CraftSessionMarketEvidence MarketEvidence,
        CraftSessionProcurementOverlay? ProcurementOverlay,
        CraftSessionViewState ViewState,
        CraftSessionVersionStamp Versions,
        HashSet<CraftSessionDirtyBucket> DirtyBuckets,
        int ChangeCount,
        int DeferredChangeCount);

    private sealed class PlanSessionToken
    {
        public PlanSessionToken(long version, CraftSessionVersionStamp stamp)
        {
            Version = version;
            Stamp = stamp;
        }

        public long Version { get; }
        public CraftSessionVersionStamp Stamp { get; }
    }
}
