using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect.UI;

/// <summary>
/// Implementation of IUiServices - aggregates all UI layer services.
/// </summary>
public class UiServices : IUiServices
{
    public IMarketPlansRenderer PlansRenderer { get; }
    public ICardFactory Cards { get; }
    public IDialogService Dialogs { get; }

    public UiServices(
        IMarketPlansRenderer marketPlansRenderer,
        ICardFactory cardFactory,
        IDialogService dialogService)
    {
        PlansRenderer = marketPlansRenderer;
        Cards = cardFactory;
        Dialogs = dialogService;
    }
}
