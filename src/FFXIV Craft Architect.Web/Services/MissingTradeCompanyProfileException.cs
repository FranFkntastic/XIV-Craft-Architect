namespace FFXIV_Craft_Architect.Web.Services;

public sealed class MissingTradeCompanyProfileException : InvalidOperationException
{
    public MissingTradeCompanyProfileException(
        Guid companyProfileId,
        string childKind,
        string childId)
        : base(
            $"Trade {childKind} '{childId}' references missing company profile " +
            $"'{companyProfileId:D}'.")
    {
        CompanyProfileId = companyProfileId;
    }

    public Guid CompanyProfileId { get; }
}
