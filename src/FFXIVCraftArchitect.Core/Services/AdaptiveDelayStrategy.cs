namespace FFXIVCraftArchitect.Core.Services;

/// <summary>
/// Adaptive delay strategy for API rate limiting.
/// Starts with aggressive (fast) delays and scales up when rate limits are hit.
/// </summary>
public class AdaptiveDelayStrategy
{
    private int _currentDelayMs;
    private int _consecutiveFailures;
    private readonly int _minDelay;
    private readonly int _maxDelay;
    private readonly double _backoffMultiplier;
    private readonly double _rateLimitMultiplier;

    /// <summary>
    /// Creates a new adaptive delay strategy.
    /// </summary>
    /// <param name="initialDelayMs">Initial delay in milliseconds (default: 100ms)</param>
    /// <param name="minDelayMs">Minimum delay in milliseconds (default: 50ms)</param>
    /// <param name="maxDelayMs">Maximum delay in milliseconds (default: 5000ms)</param>
    /// <param name="backoffMultiplier">Multiplier for regular failures (default: 2.0)</param>
    /// <param name="rateLimitMultiplier">Multiplier for 429 rate limit errors (default: 3.0)</param>
    public AdaptiveDelayStrategy(
        int initialDelayMs = 100,
        int minDelayMs = 50,
        int maxDelayMs = 5000,
        double backoffMultiplier = 2.0,
        double rateLimitMultiplier = 3.0)
    {
        _currentDelayMs = initialDelayMs;
        _minDelay = minDelayMs;
        _maxDelay = maxDelayMs;
        _backoffMultiplier = backoffMultiplier;
        _rateLimitMultiplier = rateLimitMultiplier;
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Gets the current delay in milliseconds.
    /// </summary>
    public int GetDelay() => _currentDelayMs;

    /// <summary>
    /// Reports a successful request.
    /// Resets failure counter but doesn't reduce delay (keeps current speed).
    /// </summary>
    public void ReportSuccess()
    {
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Reports a failed request.
    /// Increases delay based on the type of failure.
    /// </summary>
    /// <param name="statusCode">HTTP status code of the failed request</param>
    public void ReportFailure(System.Net.HttpStatusCode statusCode)
    {
        _consecutiveFailures++;
        
        // Use higher multiplier for rate limit errors (429)
        var multiplier = statusCode == System.Net.HttpStatusCode.TooManyRequests 
            ? _rateLimitMultiplier 
            : _backoffMultiplier;
        
        var newDelay = (int)(_currentDelayMs * multiplier);
        _currentDelayMs = Math.Min(_maxDelay, newDelay);
    }

    /// <summary>
    /// Gets diagnostic information about the current state.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"Delay: {_currentDelayMs}ms, ConsecutiveFailures: {_consecutiveFailures}";
    }
}
