namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class CompanyOwnershipTransferReconciler(
    CompanyOwnershipTransferService transfers,
    ILogger<CompanyOwnershipTransferReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var completed = await transfers.ReconcilePendingAsync(stoppingToken);
                if (completed > 0)
                {
                    logger.LogInformation("Completed {Count} pending company ownership membership projections.", completed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Company ownership transfer reconciliation failed; it will retry automatically.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
