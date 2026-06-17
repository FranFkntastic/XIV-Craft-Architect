using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Dialogs;

public sealed record TradeOrderCloseDialogResult(TradeOrderStatus Status, string? Note);
