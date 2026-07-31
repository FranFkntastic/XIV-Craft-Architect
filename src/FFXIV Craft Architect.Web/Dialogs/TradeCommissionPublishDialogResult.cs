using FFXIV_Craft_Architect.Web.Services.TradeCompany;

namespace FFXIV_Craft_Architect.Web.Dialogs;

public sealed record TradeCommissionPublishDialogResult(
    TradeCommissionDestination Destination,
    bool IsTestFixture);
