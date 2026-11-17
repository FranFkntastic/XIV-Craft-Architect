using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

// Runs the duplicate-crafter repair at startup, as a logged dry run by default. When an
// operator sets Apply, a group that loses a write race with live traffic is retried a
// few times; anything still failing waits for the next start.
public sealed class CompanyCrafterConsolidationHostedService(
    ProfileHostOptions profileOptions,
    CompanyCrafterConsolidationOptions options,
    CompanyCrafterConsolidationService consolidation,
    ILogger<CompanyCrafterConsolidationHostedService> logger) : BackgroundService
{
    private const int MaximumAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!profileOptions.Enabled || options.Mode == CompanyCrafterConsolidationMode.Disabled)
        {
            return;
        }

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            CompanyCrafterConsolidationReport report;
            try
            {
                report = await consolidation.RunAsync(
                    options.Mode == CompanyCrafterConsolidationMode.DryRun,
                    stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Crafter consolidation could not run.");
                return;
            }

            var failed = report.Merges.Count(item => item.Status == "failed");
            logger.LogInformation(
                "Crafter consolidation {Mode}: {Merged} merged, {Planned} planned, {Failed} failed, {Skipped} skipped.",
                options.Mode,
                report.Merges.Count(item => item.Status == "merged"),
                report.Merges.Count(item => item.Status == "planned"),
                failed,
                report.Skipped.Count);

            if (failed == 0 || attempt == MaximumAttempts)
            {
                return;
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }
}
