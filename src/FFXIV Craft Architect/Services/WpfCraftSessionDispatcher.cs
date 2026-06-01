using System.Windows;
using System.Windows.Threading;
using FFXIV_Craft_Architect.Core.Services;

namespace FFXIV_Craft_Architect.Services;

public sealed class WpfCraftSessionDispatcher : ICraftSessionDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfCraftSessionDispatcher()
        : this(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
    {
    }

    internal WpfCraftSessionDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public void Dispatch(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }
}
