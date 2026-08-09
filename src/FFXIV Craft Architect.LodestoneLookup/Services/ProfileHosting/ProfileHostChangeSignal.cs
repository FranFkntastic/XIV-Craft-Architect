using System.Collections.Concurrent;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileHostChangeSignal
{
    private readonly ConcurrentDictionary<string, ProfileState> _profiles =
        new(StringComparer.Ordinal);

    public ProfileHostChangeObservation Observe(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        var state = _profiles.GetOrAdd(profileId, static _ => new ProfileState());
        lock (state.Gate)
        {
            return new ProfileHostChangeObservation(
                state.Generation,
                state.LastPublishedRevision,
                state.Changed.Task);
        }
    }

    public void Publish(string profileId, long serverRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serverRevision);
        var state = _profiles.GetOrAdd(profileId, static _ => new ProfileState());
        TaskCompletionSource changed;
        lock (state.Gate)
        {
            if (serverRevision <= state.LastPublishedRevision)
            {
                return;
            }

            state.Generation++;
            state.LastPublishedRevision = serverRevision;
            changed = state.Changed;
            state.Changed = NewSignal();
        }

        changed.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ProfileState
    {
        public object Gate { get; } = new();
        public long Generation { get; set; }
        public long LastPublishedRevision { get; set; }
        public TaskCompletionSource Changed { get; set; } = NewSignal();
    }
}

public readonly record struct ProfileHostChangeObservation(
    long Generation,
    long LastPublishedRevision,
    Task Changed);
