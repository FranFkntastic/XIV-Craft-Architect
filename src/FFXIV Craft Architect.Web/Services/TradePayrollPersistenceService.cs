using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services;

public interface ITradePayrollDraftStore
{
    Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId);
    Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft);
    Task<bool> DeleteDraftAsync(string draftId);
}

public sealed class IndexedDbTradePayrollDraftStore : ITradePayrollDraftStore
{
    private readonly IndexedDbService _indexedDb;

    public IndexedDbTradePayrollDraftStore(IndexedDbService indexedDb)
    {
        _indexedDb = indexedDb;
    }

    public async Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId)
    {
        return await _indexedDb.LoadTradePayrollDraftsAsync(companyProfileId);
    }

    public async Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft)
    {
        return await _indexedDb.SaveTradePayrollDraftAsync(draft);
    }

    public async Task<bool> DeleteDraftAsync(string draftId)
    {
        return await _indexedDb.DeleteTradePayrollDraftAsync(draftId);
    }
}

public sealed class TradePayrollPersistenceService
{
    private readonly ITradePayrollDraftStore _store;

    public TradePayrollPersistenceService(ITradePayrollDraftStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<TradePayrollWorkflowDraft>> LoadDraftsAsync(Guid companyProfileId)
    {
        return await _store.LoadDraftsAsync(companyProfileId);
    }

    public async Task<TradePayrollWorkflowDraft> GetOrCreateDraftAsync(
        Guid companyProfileId,
        Guid? orderId,
        long planSessionVersion,
        long marketAnalysisVersion,
        string sourcePlanName,
        Guid? assignedCrafterId,
        string? assignedCrafterDisplayName)
    {
        var drafts = await _store.LoadDraftsAsync(companyProfileId);
        var existing = orderId.HasValue
            ? drafts.FirstOrDefault(draft => draft.OrderId == orderId.Value)
            : drafts.FirstOrDefault(draft => !draft.OrderId.HasValue && draft.PlanSessionVersion == planSessionVersion);
        if (existing != null)
        {
            return existing;
        }

        var now = DateTime.UtcNow;
        var draft = new TradePayrollWorkflowDraft
        {
            CompanyProfileId = companyProfileId,
            OrderId = orderId,
            PlanSessionVersion = planSessionVersion,
            MarketAnalysisVersion = marketAnalysisVersion,
            SourcePlanName = string.IsNullOrWhiteSpace(sourcePlanName) ? "Active craft plan" : sourcePlanName,
            AssignedCrafterId = assignedCrafterId,
            AssignedCrafterDisplayName = assignedCrafterDisplayName,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _store.SaveDraftAsync(draft);
        return draft;
    }

    public async Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft)
    {
        draft.UpdatedAtUtc = DateTime.UtcNow;
        return await _store.SaveDraftAsync(draft);
    }

    public IReadOnlyList<CommissionPayrollInputLine> ApplyResponsibilities(
        IReadOnlyList<CommissionPayrollInputLine> regeneratedLines,
        TradePayrollWorkflowDraft draft)
    {
        var responsibilities = draft.Responsibilities.ToDictionary(
            line => (line.ItemId, line.RequiresHq),
            line => line.Responsibility);

        return regeneratedLines
            .Select(line => responsibilities.TryGetValue((line.ItemId, line.RequiresHq), out var responsibility)
                ? line with { Responsibility = responsibility }
                : line)
            .ToArray();
    }
}
