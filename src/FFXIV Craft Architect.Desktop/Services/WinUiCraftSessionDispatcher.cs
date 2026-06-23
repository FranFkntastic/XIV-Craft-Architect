using FFXIV_Craft_Architect.Core.Services;
using Microsoft.UI.Dispatching;

namespace FFXIV_Craft_Architect.Desktop.Services;

public sealed class WinUiCraftSessionDispatcher : ICraftSessionDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue;

    public WinUiCraftSessionDispatcher()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinUI dispatcher queue is not available on the current thread.");
    }

    public void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        if (!_dispatcherQueue.TryEnqueue(() => action()))
        {
            throw new InvalidOperationException("Unable to enqueue Core session change on the WinUI dispatcher.");
        }
    }
}
