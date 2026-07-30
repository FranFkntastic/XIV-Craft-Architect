using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

public enum DiscordApiOutcome
{
    Succeeded,
    RetryableFailure,
    TerminalFailure,
    ReconciliationRequired
}

public sealed record DiscordApiResult(
    DiscordApiOutcome Outcome,
    string? MessageId = null,
    HttpStatusCode? StatusCode = null,
    string? Error = null,
    TimeSpan? RetryAfter = null)
{
    public bool Succeeded => Outcome == DiscordApiOutcome.Succeeded;
}

public interface IDiscordApiClient
{
    Task<DiscordApiResult> CreateMessageAsync(
        string channelId,
        object payload,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> EditMessageAsync(
        string channelId,
        string messageId,
        object payload,
        CancellationToken cancellationToken = default);

    Task<DiscordApiResult> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default);
}

public sealed class DiscordApiClient(
    HttpClient httpClient,
    DiscordCommissionOptions options,
    ILogger<DiscordApiClient> logger) : IDiscordApiClient
{
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(30);

    public Task<DiscordApiResult> CreateMessageAsync(
        string channelId,
        object payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Post,
            channelId,
            messageId: null,
            payload,
            createOperation: true,
            cancellationToken);

    public Task<DiscordApiResult> EditMessageAsync(
        string channelId,
        string messageId,
        object payload,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Patch,
            channelId,
            messageId,
            payload,
            createOperation: false,
            cancellationToken);

    public Task<DiscordApiResult> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            channelId,
            messageId,
            payload: null,
            createOperation: false,
            cancellationToken);

    private async Task<DiscordApiResult> SendAsync(
        HttpMethod method,
        string channelId,
        string? messageId,
        object? payload,
        bool createOperation,
        CancellationToken cancellationToken)
    {
        if (!options.CanPublishDirectly ||
            !IsDiscordSnowflake(channelId) ||
            (method != HttpMethod.Post && string.IsNullOrWhiteSpace(messageId)) ||
            (payload != null && !HasMentionSuppression(payload)))
        {
            return new DiscordApiResult(
                DiscordApiOutcome.TerminalFailure,
                Error: "Discord publication is disabled, has an invalid channel, or lacks mention suppression.");
        }

        var relativePath = messageId == null
            ? $"channels/{Uri.EscapeDataString(channelId)}/messages"
            : $"channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";

        for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bot", options.BotToken);
            request.Headers.UserAgent.ParseAdd("FFXIV-Craft-Architect/1.0");
            if (payload != null)
            {
                request.Content = JsonContent.Create(payload);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DiscordApiResult(
                    createOperation
                        ? DiscordApiOutcome.ReconciliationRequired
                        : DiscordApiOutcome.RetryableFailure,
                    Error: "Discord request timed out.");
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Discord {Method} {Path} failed before a response.", method, relativePath);
                return new DiscordApiResult(
                    createOperation
                        ? DiscordApiOutcome.ReconciliationRequired
                        : DiscordApiOutcome.RetryableFailure,
                    Error: "Discord request failed before a response.");
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var id = await ReadMessageIdAsync(response, cancellationToken);
                    return method == HttpMethod.Get || !string.IsNullOrWhiteSpace(id)
                        ? new DiscordApiResult(DiscordApiOutcome.Succeeded, id, response.StatusCode)
                        : new DiscordApiResult(
                            createOperation
                                ? DiscordApiOutcome.ReconciliationRequired
                                : DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: "Discord returned success without a message identity.");
                }

                var error = await ReadErrorAsync(response, cancellationToken);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = await ReadRetryAfterAsync(response, cancellationToken);
                    if (!retryAfter.HasValue ||
                        retryAfter.Value < TimeSpan.Zero ||
                        retryAfter.Value > MaximumRetryAfter)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.TerminalFailure,
                            StatusCode: response.StatusCode,
                            Error: "Discord returned an invalid retry interval.");
                    }

                    if (attempt == MaximumAttempts)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: error,
                            RetryAfter: retryAfter);
                    }

                    await Task.Delay(retryAfter.Value, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode >= 500)
                {
                    if (createOperation)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.ReconciliationRequired,
                            StatusCode: response.StatusCode,
                            Error: error);
                    }

                    if (attempt == MaximumAttempts)
                    {
                        return new DiscordApiResult(
                            DiscordApiOutcome.RetryableFailure,
                            StatusCode: response.StatusCode,
                            Error: error);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1))), cancellationToken);
                    continue;
                }

                return new DiscordApiResult(
                    DiscordApiOutcome.TerminalFailure,
                    StatusCode: response.StatusCode,
                    Error: error);
            }
        }

        return new DiscordApiResult(
            DiscordApiOutcome.RetryableFailure,
            Error: "Discord request exhausted its retry budget.");
    }

    private static bool IsDiscordSnowflake(string value) =>
        value.Length is >= 17 and <= 20 &&
        value.All(char.IsAsciiDigit);

    private static bool HasMentionSuppression(object payload)
    {
        try
        {
            var json = JsonSerializer.SerializeToElement(payload);
            return json.TryGetProperty("allowed_mentions", out var allowedMentions) &&
                allowedMentions.TryGetProperty("parse", out var parse) &&
                parse.ValueKind == JsonValueKind.Array &&
                parse.GetArrayLength() == 0;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadMessageIdAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            return payload.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> ReadErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (text.Length > 512)
        {
            text = text[..512];
        }

        return string.IsNullOrWhiteSpace(text)
            ? $"Discord returned HTTP {(int)response.StatusCode}."
            : text;
    }

    private static async Task<TimeSpan?> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            using var payload = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (payload.RootElement.TryGetProperty("retry_after", out var retryAfter) &&
                retryAfter.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
            // Fall through to the standard Retry-After header.
        }

        return response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is { } retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : null);
    }
}
