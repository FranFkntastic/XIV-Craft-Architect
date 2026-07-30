using System.Collections.Concurrent;
using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CraftAppraisal;

public interface IHostedCraftAppraisalCoordinator
{
    bool IsAvailable { get; }

    Task<CraftAppraisalQuote> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken);
}

public sealed class HostedCraftAppraisalCoordinator : IHostedCraftAppraisalCoordinator
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly CraftAppraisalApiOptions options;
    private readonly CraftAppraisalPlanStore planStore;
    private readonly IMemoryCache cache;
    private readonly SemaphoreSlim concurrency;
    private readonly ConcurrentDictionary<QuoteKey, Lazy<Task<CraftAppraisalQuote>>> inFlight = new();

    public HostedCraftAppraisalCoordinator(
        IServiceScopeFactory scopeFactory,
        CraftAppraisalApiOptions options,
        CraftAppraisalPlanStore planStore,
        IMemoryCache cache)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        this.planStore = planStore;
        this.cache = cache;
        concurrency = new SemaphoreSlim(options.MaximumConcurrentQuotes);
    }

    public bool IsAvailable => options.Enabled;

    public async Task<CraftAppraisalQuote> AppraiseAsync(
        CraftAppraisalRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Hosted craft appraisal is disabled.");

        var key = QuoteKey.From(request);
        if (cache.TryGetValue(key, out CraftAppraisalQuote? cached) && cached != null)
            return cached;

        var pending = inFlight.GetOrAdd(
            key,
            static (quoteKey, state) => new Lazy<Task<CraftAppraisalQuote>>(
                () => state.Coordinator.ComputeAndRemoveAsync(quoteKey, state.Request),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Coordinator: this, Request: request));

        return await pending.Value.WaitAsync(cancellationToken);
    }

    private async Task<CraftAppraisalQuote> ComputeAndRemoveAsync(
        QuoteKey key,
        CraftAppraisalRequest request)
    {
        try
        {
            return await ComputeAsync(key, request);
        }
        finally
        {
            inFlight.TryRemove(key, out _);
        }
    }

    private async Task<CraftAppraisalQuote> ComputeAsync(
        QuoteKey key,
        CraftAppraisalRequest request)
    {
        using var timeout = new CancellationTokenSource(options.QuoteTimeout);
        await concurrency.WaitAsync(timeout.Token);
        try
        {
            if (cache.TryGetValue(key, out CraftAppraisalQuote? cached) && cached != null)
                return cached;

            using var scope = scopeFactory.CreateScope();
            var appraisal = scope.ServiceProvider.GetRequiredService<ICraftAppraisalService>();
            var serializer = scope.ServiceProvider.GetRequiredService<RecipeCalculationService>();
            var quote = await appraisal.AppraiseAsync(request, timeout.Token);
            if (quote.Plan != null)
            {
                var planJson = serializer.SerializePlan(quote.Plan, includeMarketPrices: true);
                var planId = await planStore.SaveAsync(planJson, timeout.Token);
                var snapshotUrl = $"{options.PublicApiOrigin}/craft/plans/{planId}";
                quote = quote with
                {
                    PlanId = planId,
                    PlanUrl = $"{options.PublicAppOrigin}/?appraisalPlan={Uri.EscapeDataString(snapshotUrl)}",
                };
            }

            quote = quote with { Source = "CraftArchitectHosted" };
            cache.Set(key, quote, options.QuoteCacheLifetime);
            return quote;
        }
        finally
        {
            concurrency.Release();
        }
    }

    private sealed record QuoteKey(
        uint ItemId,
        string ItemName,
        uint Quantity,
        string Region,
        string DataCenter,
        string World,
        string HqPolicy,
        string PricingMode)
    {
        public static QuoteKey From(CraftAppraisalRequest request) => new(
            request.ItemId,
            request.ItemName.Trim(),
            request.Quantity,
            request.Scope.Region.Trim(),
            request.Scope.DataCenter?.Trim() ?? string.Empty,
            request.Scope.World?.Trim() ?? string.Empty,
            request.Options.HqPolicy.Trim(),
            request.Options.PricingMode.Trim());
    }
}
