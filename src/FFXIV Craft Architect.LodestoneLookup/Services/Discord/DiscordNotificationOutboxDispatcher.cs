using System.Text.Json;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public sealed class DiscordNotificationOutboxDispatcher(
    SqliteDiscordNotificationStore store,
    IDiscordApiClient discord,
    DiscordCommissionOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordNotificationOutboxDispatcher> logger) : BackgroundService
{
    private const int MaximumBatchSize = 20;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.CanPublishDirectly)
                {
                    await DispatchDueAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Discord commission notification dispatch failed closed.");
            }

            await Task.Delay(options.OutboxPollInterval, timeProvider, stoppingToken);
        }
    }

    internal async Task DispatchDueAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var due = await store.LeaseDueAsync(
            now,
            options.OutboxLeaseDuration,
            MaximumBatchSize,
            cancellationToken);
        foreach (var workItem in due)
        {
            await DispatchAsync(workItem, cancellationToken);
        }
    }

    private async Task DispatchAsync(
        DiscordNotificationOutboxWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (!await store.MatchesCurrentRouteAsync(workItem, cancellationToken))
        {
            await store.FailAsync(
                workItem,
                DiscordOutboxState.Failed,
                "route_changed",
                "The persisted notification destination no longer matches the revisioned company route.",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return;
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(workItem.PayloadJson);
            payload = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            await store.FailAsync(
                workItem,
                DiscordOutboxState.Failed,
                "invalid_payload",
                $"Stored Discord notification payload is invalid: {exception.Message}",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return;
        }

        var channelId = workItem.ChannelId;
        if (workItem.DestinationKind ==
                DiscordNotificationDestinationKind.CommissionerDirectMessage &&
            string.IsNullOrWhiteSpace(channelId))
        {
            var channelResult = await discord.ResolveDirectMessageChannelAsync(
                workItem.CommissionerDiscordUserId,
                cancellationToken);
            if (!channelResult.Succeeded ||
                !DiscordSnowflake.IsValid(channelResult.MessageId))
            {
                await HandleFailureAsync(
                    workItem,
                    channelResult,
                    "dm_channel_unavailable",
                    cancellationToken);
                return;
            }

            channelId = channelResult.MessageId;
            await store.SetResolvedChannelAsync(
                workItem.WorkItemId,
                workItem.LeaseId,
                channelId!,
                timeProvider.GetUtcNow(),
                cancellationToken);
        }

        if (!DiscordSnowflake.IsValid(channelId))
        {
            await store.FailAsync(
                workItem,
                DiscordOutboxState.Failed,
                "invalid_destination",
                "The Discord notification destination is missing or invalid.",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return;
        }

        var result = await discord.CreateNotificationMessageAsync(
            channelId!,
            payload,
            workItem.AllowedMentionUserId,
            cancellationToken);
        if (result.Succeeded)
        {
            await store.CompleteAsync(
                workItem.WorkItemId,
                workItem.LeaseId,
                result.MessageId,
                timeProvider.GetUtcNow(),
                cancellationToken);
            return;
        }

        await HandleFailureAsync(
            workItem,
            result,
            "discord_delivery_failed",
            cancellationToken);
    }

    private async Task HandleFailureAsync(
        DiscordNotificationOutboxWorkItem workItem,
        DiscordApiResult result,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        switch (result.Outcome)
        {
            case DiscordApiOutcome.ReconciliationRequired:
                await store.FailAsync(
                    workItem,
                    DiscordOutboxState.ReconciliationRequired,
                    "ambiguous_create",
                    result.Error ??
                        "Discord message creation has an ambiguous outcome.",
                    now,
                    cancellationToken);
                return;

            case DiscordApiOutcome.TerminalFailure:
                await store.FailAsync(
                    workItem,
                    DiscordOutboxState.Failed,
                    failureCode,
                    result.Error ?? "Discord rejected the notification.",
                    now,
                    cancellationToken);
                return;

            case DiscordApiOutcome.RetryableFailure
                when workItem.AttemptCount >= options.OutboxMaximumAttempts:
                await store.FailAsync(
                    workItem,
                    DiscordOutboxState.Failed,
                    "retry_budget_exhausted",
                    result.Error ??
                        "Discord notification retry budget was exhausted.",
                    now,
                    cancellationToken);
                return;

            case DiscordApiOutcome.RetryableFailure:
                var retryDelay = result.RetryAfter ?? TimeSpan.FromSeconds(
                    Math.Min(
                        1 << Math.Clamp(workItem.AttemptCount, 0, 8),
                        MaximumRetryDelay.TotalSeconds));
                if (retryDelay > MaximumRetryDelay)
                {
                    retryDelay = MaximumRetryDelay;
                }

                await store.RetryAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    now + retryDelay,
                    result.Error ?? "Discord notification remains retryable.",
                    cancellationToken);
                return;

            default:
                await store.FailAsync(
                    workItem,
                    DiscordOutboxState.Failed,
                    failureCode,
                    result.Error ?? "Discord notification failed.",
                    now,
                    cancellationToken);
                return;
        }
    }
}
