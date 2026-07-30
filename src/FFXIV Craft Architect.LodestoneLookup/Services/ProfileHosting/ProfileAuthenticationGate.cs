namespace FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

public sealed class ProfileAuthenticationGate
{
    private const int MaximumAccessKeyLength = 256;
    private const int MaximumConcurrentAuthentications = 2;
    private const int MaximumAuthenticationsPerWindow = 12;
    private static readonly TimeSpan AuthenticationWindow = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _concurrency =
        new(MaximumConcurrentAuthentications, MaximumConcurrentAuthentications);
    private readonly object _windowLock = new();
    private DateTimeOffset _windowStartedAt = DateTimeOffset.UtcNow;
    private int _windowAttempts;

    public async Task<T?> ExecuteAsync<T>(
        string plaintextKey,
        Func<CancellationToken, Task<T?>> authenticate,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(authenticate);
        if (!IsBoundedAccessKey(plaintextKey) ||
            !TryConsumeWindowPermit() ||
            !await _concurrency.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        try
        {
            return await authenticate(cancellationToken);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private bool TryConsumeWindowPermit()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_windowLock)
        {
            if (now - _windowStartedAt >= AuthenticationWindow)
            {
                _windowStartedAt = now;
                _windowAttempts = 0;
            }

            if (_windowAttempts >= MaximumAuthenticationsPerWindow)
            {
                return false;
            }

            _windowAttempts++;
            return true;
        }
    }

    private static bool IsBoundedAccessKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumAccessKeyLength;
}
