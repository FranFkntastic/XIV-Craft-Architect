using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Dialogs;

public sealed record TradeCompanyConnectionDialogResult(
    CompanyId CompanyId,
    string ServiceUrl,
    string AccessKey);
