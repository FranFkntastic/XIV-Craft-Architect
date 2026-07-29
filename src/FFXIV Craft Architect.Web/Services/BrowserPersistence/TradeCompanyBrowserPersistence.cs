using FFXIV_Craft_Architect.Core.Models;
using Microsoft.JSInterop;

namespace FFXIV_Craft_Architect.Web.Services.BrowserPersistence;

public sealed record BrowserTradeCompanyMutationOutboxEntry(
    string Key,
    CompanyId CompanyId,
    string State,
    TradeCompanyMutationRequest Request,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int AttemptCount,
    TradeCompanyMutationResult? Result);

/// <summary>
/// Company-scoped browser cache and mutation outbox. Canonical company identity is
/// always the namespace; deployment origin and client build never participate in keys.
/// </summary>
public sealed class TradeCompanyBrowserPersistence
{
    private readonly IJSRuntime _jsRuntime;

    public TradeCompanyBrowserPersistence(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task SaveIdentityAsync(
        TradeCompanyIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.saveCachedTradeCompanyIdentity",
            cancellationToken,
            identity);
    }

    public async Task<TradeCompanyIdentity?> LoadIdentityAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<TradeCompanyIdentity?>(
            "IndexedDB.loadCachedTradeCompanyIdentity",
            cancellationToken,
            companyId.ToString());
    }

    public async Task ApplyChangeSetAsync(
        TradeCompanyChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.applyTradeCompanyChangeSet",
            cancellationToken,
            changeSet);
    }

    public async Task<TradeCompanyChangeSet> LoadChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<TradeCompanyChangeSet>(
            "IndexedDB.loadCachedTradeCompanyChanges",
            cancellationToken,
            companyId.ToString(),
            afterRevision.Value);
    }

    public async Task<TradeCompanyRecordEnvelope?> LoadRecordAsync(
        CompanyId companyId,
        string recordKind,
        string recordId,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<TradeCompanyRecordEnvelope?>(
            "IndexedDB.loadCachedTradeCompanyRecord",
            cancellationToken,
            companyId.ToString(),
            recordKind,
            recordId);
    }

    public async Task<BrowserTradeCompanyMutationOutboxEntry> EnqueueAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<BrowserTradeCompanyMutationOutboxEntry>(
            "IndexedDB.enqueueTradeCompanyMutation",
            cancellationToken,
            request);
    }

    public async Task<IReadOnlyList<BrowserTradeCompanyMutationOutboxEntry>> LoadOutboxAsync(
        CompanyId companyId,
        bool includeTerminal = false,
        CancellationToken cancellationToken = default)
    {
        return await _jsRuntime.InvokeAsync<BrowserTradeCompanyMutationOutboxEntry[]>(
            "IndexedDB.loadTradeCompanyMutationOutbox",
            cancellationToken,
            companyId.ToString(),
            includeTerminal);
    }

    public async Task RecordAttemptAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeAsync<BrowserTradeCompanyMutationOutboxEntry>(
            "IndexedDB.markTradeCompanyMutationAttempt",
            cancellationToken,
            request.CompanyId.ToString(),
            request.IdempotencyKey);
    }

    public async Task CompleteAsync(
        TradeCompanyMutationRequest request,
        TradeCompanyMutationResult result,
        CancellationToken cancellationToken = default)
    {
        await _jsRuntime.InvokeAsync<bool>(
            "IndexedDB.completeTradeCompanyMutation",
            cancellationToken,
            request.CompanyId.ToString(),
            request.IdempotencyKey,
            result);
    }
}
