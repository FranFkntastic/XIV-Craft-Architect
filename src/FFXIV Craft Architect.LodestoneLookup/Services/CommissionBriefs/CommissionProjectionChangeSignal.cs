using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

public sealed class CommissionProjectionChangeSignal
{
    private readonly ConcurrentDictionary<string, ProjectionState> _projections =
        new(StringComparer.Ordinal);

    public CommissionProjectionChangeObservation Observe(string publicId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        var state = _projections.GetOrAdd(publicId, static _ => new ProjectionState());
        lock (state.Gate)
        {
            return new CommissionProjectionChangeObservation(
                state.Generation,
                state.Changed.Task);
        }
    }

    public void Publish(string publicId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicId);
        var state = _projections.GetOrAdd(publicId, static _ => new ProjectionState());
        TaskCompletionSource changed;
        lock (state.Gate)
        {
            state.Generation++;
            changed = state.Changed;
            state.Changed = NewSignal();
        }
        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ProjectionState
    {
        public object Gate { get; } = new();
        public long Generation { get; set; }
        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }
}

public readonly record struct CommissionProjectionChangeObservation(
    long Generation,
    Task Changed);

public sealed class CommissionProjectionChangePostCommitSink(
    CommissionProjectionChangeSignal signal) : ICompanyCommissionPostCommitSink
{
    public Task OnCommittedAsync(
        TradeCompanyAccessContext access,
        HostedCompanyCommissionSnapshot committed,
        CompanyCommissionActivityEvent activity,
        CancellationToken cancellationToken)
    {
        var publicId = committed.Order.CompanyCommission?.PublicMetadata.PublicBriefId;
        if (!string.IsNullOrWhiteSpace(publicId))
        {
            signal.Publish(publicId);
        }
        return Task.CompletedTask;
    }
}

public static class CommissionProjectionTag
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static string Create(object projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            projection,
            projection.GetType(),
            JsonOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    public static bool IsValid(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
