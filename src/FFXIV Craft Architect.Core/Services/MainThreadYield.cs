namespace FFXIV_Craft_Architect.Core.Services;

/// <summary>
/// Hands the thread back between slices of long interactive work so a page can render and
/// take input. The default is a 1 ms timer; a browser host replaces it, because browsers
/// throttle timers in a background tab to one per second or slower, which turns a few dozen
/// yields into minutes of waiting.
/// </summary>
public static class MainThreadYield
{
    /// <summary>How to yield. Set once at startup by a host with a cheaper, unthrottled way.</summary>
    public static Func<CancellationToken, Task> Yielder { get; set; } = ct => Task.Delay(1, ct);

    public static Task Async(CancellationToken ct = default) => Yielder(ct);
}
