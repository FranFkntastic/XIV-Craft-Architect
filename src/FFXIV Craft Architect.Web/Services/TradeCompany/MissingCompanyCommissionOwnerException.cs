namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public sealed class MissingCompanyCommissionOwnerException : InvalidOperationException
{
    public MissingCompanyCommissionOwnerException(
        Guid companyId,
        Guid commissionId,
        string? message = null)
        : base(message ??
            "The hosted canonical commission no longer exists for this Trade order.")
    {
        CompanyId = companyId;
        CommissionId = commissionId;
    }

    public Guid CompanyId { get; }

    public Guid CommissionId { get; }
}
