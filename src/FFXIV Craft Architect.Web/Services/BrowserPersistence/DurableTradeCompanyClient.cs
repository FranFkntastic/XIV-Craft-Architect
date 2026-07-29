using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Web.Services.BrowserPersistence;

/// <summary>
/// Adds browser caching and write-ahead durability to a canonical company transport.
/// A mutation is durable before transport begins; transport failures are rethrown and
/// leave the request pending for an explicit replay.
/// </summary>
public sealed class DurableTradeCompanyClient : ITradeCompanyClient
{
    private readonly ITradeCompanyClient _transport;
    private readonly TradeCompanyBrowserPersistence _browser;

    public DurableTradeCompanyClient(
        ITradeCompanyClient transport,
        TradeCompanyBrowserPersistence browser)
    {
        _transport = transport;
        _browser = browser;
    }

    public async Task<TradeCompanyIdentity?> GetCompanyAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var identity = await _transport.GetCompanyAsync(companyId, cancellationToken);
        if (identity is not null)
        {
            await _browser.SaveIdentityAsync(identity, cancellationToken);
        }
        return identity;
    }

    public async Task<TradeCompanyChangeSet> GetChangesAsync(
        CompanyId companyId,
        CompanyRevision afterRevision,
        CancellationToken cancellationToken = default)
    {
        if (await _browser.LoadIdentityAsync(companyId, cancellationToken) is null)
        {
            var identity = await _transport.GetCompanyAsync(companyId, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Company {companyId} cannot be cached because its canonical identity is unavailable.");
            await _browser.SaveIdentityAsync(identity, cancellationToken);
        }
        var changes = await _transport.GetChangesAsync(
            companyId,
            afterRevision,
            cancellationToken);
        await _browser.ApplyChangeSetAsync(changes, cancellationToken);
        return changes;
    }

    public async Task<TradeCompanyMutationResult> MutateAsync(
        TradeCompanyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        await _browser.EnqueueAsync(request, cancellationToken);
        await _browser.RecordAttemptAsync(request, cancellationToken);
        var result = await _transport.MutateAsync(request, cancellationToken);
        await _browser.CompleteAsync(request, result, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<TradeCompanyMutationResult>> ReplayPendingAsync(
        CompanyId companyId,
        CancellationToken cancellationToken = default)
    {
        var pending = await _browser.LoadOutboxAsync(
            companyId,
            includeTerminal: false,
            cancellationToken);
        var results = new List<TradeCompanyMutationResult>(pending.Count);
        foreach (var entry in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _browser.RecordAttemptAsync(entry.Request, cancellationToken);
            var result = await _transport.MutateAsync(entry.Request, cancellationToken);
            await _browser.CompleteAsync(entry.Request, result, cancellationToken);
            results.Add(result);
        }
        return results;
    }

    public async Task<TradeCompanyMutationResult?> ReplayPendingAsync(
        CompanyId companyId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var entry = (await _browser.LoadOutboxAsync(
                companyId,
                includeTerminal: false,
                cancellationToken))
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Request.IdempotencyKey,
                    idempotencyKey,
                    StringComparison.Ordinal));
        if (entry == null)
        {
            return null;
        }

        await _browser.RecordAttemptAsync(entry.Request, cancellationToken);
        var result = await _transport.MutateAsync(entry.Request, cancellationToken);
        await _browser.CompleteAsync(entry.Request, result, cancellationToken);
        return result;
    }
}
