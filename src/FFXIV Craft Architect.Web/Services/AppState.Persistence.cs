using System.Collections.Frozen;

using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services;

public partial class AppState
{
    public void NotifySavedPlansChanged()
    {
        OnSavedPlansChanged?.Invoke();
    }

    public void ReplaceSavedPlans(IEnumerable<StoredPlanSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        SavedPlans = Array.AsReadOnly(summaries.ToArray());
        NotifySavedPlansChanged();
    }

    public void ClearSavedPlans()
    {
        if (SavedPlans.Count == 0)
        {
            return;
        }

        SavedPlans = Array.AsReadOnly(Array.Empty<StoredPlanSummary>());
        NotifySavedPlansChanged();
    }
    public StoredPlan CreateStoredPlanSnapshot(
        string planId,
        string planName,
        DateTime? savedAt = null,
        bool includeSourcePlanIdentity = false,
        bool includeLegacyMarketAnalysisFields = true)
    {
        return StoredPlanSnapshotBuilder.Build(
            this,
            planId,
            planName,
            savedAt,
            includeSourcePlanIdentity,
            includeLegacyMarketAnalysisFields);
    }

    /// <summary>
    /// Load a stored plan into the current state.
    /// </summary>
    public void LoadStoredPlan(
        StoredPlan storedPlan,
        CraftingPlan? deserializedPlan,
        bool trackStoredPlanIdentity = true)
    {
        ApplyLoadedPlanSession(
            PlanSessionLoadService.Prepare(storedPlan, deserializedPlan),
            trackStoredPlanIdentity);
    }

    public void ApplyLoadedPlanSession(
        PlanSessionLoadResult session,
        bool trackStoredPlanIdentity = true)
    {
        var storedPlan = session.StoredPlan;
        using (BeginStateChangeBatch())
        {
            SelectedDataCenter = storedPlan.DataCenter;
            ReplaceListContents(_projectItems, session.ProjectItems.Select(CloneProjectItem));
            CurrentPlan = session.Plan;
            AdvancePlanSession();
            AutoExpandItemId = null;
            ReplaceListContents(_marketItemAnalyses, session.MarketItemAnalyses);
            ReplaceListContents(_shoppingPlans, session.ShoppingPlans);
            UnavailableMarketItems = session.MarketIntelligence?.UnavailableMarketItems.ToArray()
                ?? Array.Empty<CoreMarketDataUnavailableItem>();
            _marketIntelligenceId = session.MarketIntelligence?.MarketIntelligenceId ?? Guid.Empty;
            _marketAnalysisRecipeBasis = CloneRecipeBasis(session.MarketAnalysisRecipeBasis);
            _publishedMarketAnalysisScope = session.PublishedMarketAnalysisScope;
            ClearMarketAnalysisViewState(publishChange: false);
            RecommendationMode = session.MarketIntelligence?.RecommendationMode ?? storedPlan.SavedRecommendationMode;
            MarketAnalysisLens = session.MarketIntelligence?.Lens ?? storedPlan.SavedMarketAnalysisLens;
            ClearProcurementOverlay();

            // Track the loaded plan ID for save-overwrite behavior
            if (trackStoredPlanIdentity)
            {
                CurrentPlanId = storedPlan.Id;
                CurrentPlanName = storedPlan.Name;
            }
            else
            {
                CurrentPlanId = storedPlan.SourcePlanId;
                CurrentPlanName = storedPlan.SourcePlanName;
            }

            _shoppingItems.Clear();
            SyncProjectToShopping();

            NotifySettingsChanged();
            NotifyPlanChanged();
            NotifyShoppingListChanged();
        }

        if (trackStoredPlanIdentity)
        {
            MarkPersisted(PersistedStateBucket.PlanCore | PersistedStateBucket.MarketAnalysis, CurrentVersions);
        }
    }

    public IDisposable BeginStateChangeBatch()
    {
        _changeBatchDepth++;
        return new StateChangeBatch(this);
    }

    public PersistedStateBucket GetDirtyPersistedBuckets()
    {
        var dirtyBuckets = PersistedStateBucket.None;

        if (IsDirtyVersion(_planCoreVersion, _lastPersistedPlanCoreVersion, CurrentPlan != null || _projectItems.Any()))
        {
            dirtyBuckets |= PersistedStateBucket.PlanCore;
        }

        if (IsDirtyVersion(_marketAnalysisVersion, _lastPersistedMarketAnalysisVersion, _shoppingPlans.Any() || _marketItemAnalyses.Any()))
        {
            dirtyBuckets |= PersistedStateBucket.MarketAnalysis;
        }

        return dirtyBuckets;
    }

    public bool IsPersistedBucketDirty(PersistedStateBucket bucket)
    {
        return (GetDirtyPersistedBuckets() & bucket) != PersistedStateBucket.None;
    }

    public void MarkPersisted(PersistedStateBucket buckets, AppStateVersionSnapshot versions)
    {
        if (buckets.HasFlag(PersistedStateBucket.PlanCore) &&
            versions.PlanCoreVersion == _planCoreVersion)
        {
            _lastPersistedPlanCoreVersion = versions.PlanCoreVersion;
        }

        if (buckets.HasFlag(PersistedStateBucket.MarketAnalysis) &&
            versions.MarketAnalysisVersion == _marketAnalysisVersion)
        {
            _lastPersistedMarketAnalysisVersion = versions.MarketAnalysisVersion;
        }
    }

    public bool TryBeginAutoSave(
        out AppStateVersionSnapshot capturedVersions,
        out PersistedStateBucket dirtyBuckets)
    {
        capturedVersions = CurrentVersions;
        dirtyBuckets = PersistedStateBucket.None;

        if (CurrentPlan == null && !_projectItems.Any())
        {
            return false;
        }

        if (!_autoSaveSemaphore.WaitAsync(0).GetAwaiter().GetResult())
        {
            return false;
        }

        dirtyBuckets = GetDirtyPersistedBuckets();
        if (dirtyBuckets == PersistedStateBucket.None)
        {
            _autoSaveSemaphore.Release();
            return false;
        }

        return true;
    }

    public async Task<AppStateAutoSaveLease?> BeginAutoSaveAsync(bool skipIfInFlight = false)
    {
        if (CurrentPlan == null && !_projectItems.Any())
        {
            return null;
        }

        bool acquired;
        if (skipIfInFlight)
        {
            acquired = await _autoSaveSemaphore.WaitAsync(0);
        }
        else
        {
            await _autoSaveSemaphore.WaitAsync();
            acquired = true;
        }

        if (!acquired)
        {
            return null;
        }

        var capturedVersions = CurrentVersions;
        var dirtyBuckets = GetDirtyPersistedBuckets();
        if (dirtyBuckets == PersistedStateBucket.None)
        {
            _autoSaveSemaphore.Release();
            return null;
        }

        return new AppStateAutoSaveLease(capturedVersions, dirtyBuckets);
    }

    public void CompleteAutoSave(
        bool succeeded,
        AppStateVersionSnapshot capturedVersions,
        PersistedStateBucket dirtyBuckets)
    {
        try
        {
            if (succeeded)
            {
                MarkPersisted(dirtyBuckets, capturedVersions);
            }
        }
        finally
        {
            _autoSaveSemaphore.Release();
        }
    }

    /// <summary>
    /// Clear the current plan ID (called when starting a new plan or after explicit "Save As")
    /// </summary>
    public void ClearCurrentPlanId()
    {
        TrackCurrentPlanIdentity(null, null);
    }

    public void TrackCurrentPlanIdentity(string? planId, string? planName)
    {
        CurrentPlanId = planId;
        CurrentPlanName = planName;
    }

    public bool RenameCurrentPlanIdentity(string planId, string name)
    {
        if (!string.Equals(CurrentPlanId, planId, StringComparison.Ordinal))
        {
            return false;
        }

        CurrentPlanName = name;
        return true;
    }

    /// <summary>
    /// Cached world data for data center/world selection.
    /// Loaded once and shared across all pages.
    /// </summary>
    public void StartAutoSaveTimer(Func<Task> saveCallback, int intervalSeconds = 30)
    {
        StopAutoSaveTimer();

        _autoSaveTimer = new System.Threading.Timer(
            async _ => await saveCallback(),
            null,
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>
    /// Stop the auto-save timer.
    /// </summary>
    public void StopAutoSaveTimer()
    {
        _autoSaveTimer?.Dispose();
        _autoSaveTimer = null;
    }

    public void RecordAutoSaveCompleted(DateTime completedAt)
    {
        LastAutoSave = completedAt;
        PublishChange(AppStateChangeScope.Status);
    }

}
