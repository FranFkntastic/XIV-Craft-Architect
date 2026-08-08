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
        if (!IsBoundedAccessKey(plaintextKey))
        {
            return null;
        }

        await WaitForWindowPermitAsync(cancellationToken);
        await _concurrency.WaitAsync(cancellationToken);

        try
        {
            return await authenticate(cancellationToken);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task<T?> ExecuteAsync<T>(
        string plaintextKey,
        Func<CancellationToken, Task<T?>> tryAuthenticateCached,
        Func<CancellationToken, Task<T?>> authenticate,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(tryAuthenticateCached);
        ArgumentNullException.ThrowIfNull(authenticate);
        if (!IsBoundedAccessKey(plaintextKey))
        {
            return null;
        }

        var cached = await tryAuthenticateCached(cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        await WaitForWindowPermitAsync(cancellationToken);
        await _concurrency.WaitAsync(cancellationToken);

        try
        {
            return await tryAuthenticateCached(cancellationToken) ??
                   await authenticate(cancellationToken);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task WaitForWindowPermitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan retryAfter;
            lock (_windowLock)
            {
                var now = DateTimeOffset.UtcNow;
                var elapsed = now - _windowStartedAt;
                if (elapsed >= AuthenticationWindow)
                {
                    _windowStartedAt = now;
                    _windowAttempts = 0;
                    elapsed = TimeSpan.Zero;
                }

                if (_windowAttempts < MaximumAuthenticationsPerWindow)
                {
                    _windowAttempts++;
                    return;
                }

                retryAfter = AuthenticationWindow - elapsed;
            }

            await Task.Delay(retryAfter, cancellationToken);
        }
    }

    private static bool IsBoundedAccessKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumAccessKeyLength;
}
