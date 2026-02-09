using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect.UI;

/// <summary>
/// Aggregates UI services to reduce constructor parameter bloat.
/// UI services handle view rendering, dialog display, and UI element creation.
/// </summary>
public interface IUiServices
{
    IMarketPlansRenderer PlansRenderer { get; }
    ICardFactory Cards { get; }
    IDialogService Dialogs { get; }
}
