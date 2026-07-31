using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public enum DiscordOutboxOperation
{
    CreateMessage,
    EditMessage,
    DeleteMessage
}

public sealed record DiscordOutboxWorkItem(
    Guid WorkItemId,
    string LeaseId,
    CompanyId CompanyId,
    DiscordOutboxOperation Operation,
    string ChannelId,
    string? MessageId,
    string PayloadJson,
    int AttemptCount);

public sealed class DiscordOutboxDispatcher(
    SqliteDiscordCollaborationStore store,
    IDiscordApiClient discord,
    DiscordCommissionOptions options,
    TimeProvider timeProvider,
    ILogger<DiscordOutboxDispatcher> logger) : BackgroundService
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
                logger.LogError(exception, "Discord outbox dispatch failed closed.");
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
        DiscordOutboxWorkItem workItem,
        CancellationToken cancellationToken)
    {
        if (!await store.MatchesCurrentCompanyInstallationAsync(
                workItem.CompanyId,
                workItem.ChannelId,
                cancellationToken))
        {
            await store.ExhaustAsync(
                workItem.WorkItemId,
                workItem.LeaseId,
                "The persisted Discord destination no longer matches the server-owned company installation.",
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
            await store.ExhaustAsync(
                workItem.WorkItemId,
                workItem.LeaseId,
                $"Stored Discord payload is invalid: {exception.Message}",
                timeProvider.GetUtcNow(),
                cancellationToken);
            return;
        }

        DiscordApiResult result;
        switch (workItem.Operation)
        {
            case DiscordOutboxOperation.CreateMessage:
                result = await discord.CreateMessageAsync(
                    workItem.ChannelId,
                    payload,
                    cancellationToken);
                break;
            case DiscordOutboxOperation.EditMessage when !string.IsNullOrWhiteSpace(workItem.MessageId):
                result = await discord.EditMessageAsync(
                    workItem.ChannelId,
                    workItem.MessageId,
                    payload,
                    cancellationToken);
                break;
            case DiscordOutboxOperation.DeleteMessage when !string.IsNullOrWhiteSpace(workItem.MessageId):
                result = await discord.DeleteMessageAsync(
                    workItem.ChannelId,
                    workItem.MessageId,
                    cancellationToken);
                break;
            default:
                await store.ExhaustAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    "Discord outbox operation is invalid or lacks a persisted message identity.",
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return;
        }

        var completedAt = timeProvider.GetUtcNow();
        switch (result.Outcome)
        {
            case DiscordApiOutcome.Succeeded:
                await store.CompleteAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    result.MessageId ?? workItem.MessageId,
                    completedAt,
                    cancellationToken);
                return;
            case DiscordApiOutcome.ReconciliationRequired:
                await store.RequireReconciliationAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    result.Error ?? "Discord message creation has an ambiguous outcome.",
                    completedAt,
                    cancellationToken);
                return;
            case DiscordApiOutcome.TerminalFailure:
                await store.ExhaustAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    result.Error ?? "Discord rejected the outbox operation.",
                    completedAt,
                    cancellationToken);
                return;
            case DiscordApiOutcome.RetryableFailure:
                if (workItem.AttemptCount >= options.OutboxMaximumAttempts)
                {
                    await store.ExhaustAsync(
                        workItem.WorkItemId,
                        workItem.LeaseId,
                        result.Error ?? "Discord outbox retry budget was exhausted.",
                        completedAt,
                        cancellationToken);
                    return;
                }

                var retryDelay = result.RetryAfter ?? TimeSpan.FromSeconds(
                    Math.Min(1 << Math.Clamp(workItem.AttemptCount, 0, 8), MaximumRetryDelay.TotalSeconds));
                if (retryDelay > MaximumRetryDelay)
                {
                    retryDelay = MaximumRetryDelay;
                }

                await store.RetryAsync(
                    workItem.WorkItemId,
                    workItem.LeaseId,
                    completedAt + retryDelay,
                    result.Error ?? "Discord outbox operation remains retryable.",
                    cancellationToken);
                return;
            default:
                throw new InvalidOperationException($"Unknown Discord API outcome '{result.Outcome}'.");
        }
    }
}
