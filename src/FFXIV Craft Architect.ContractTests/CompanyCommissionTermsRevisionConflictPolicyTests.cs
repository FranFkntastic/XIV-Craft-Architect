using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Pages;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionTermsRevisionConflictPolicyTests
{
    [Theory]
    [InlineData(10, 3, 10, 3, false, false)]
    [InlineData(10, 3, 11, 3, false, false)]
    [InlineData(10, 3, 10, 4, false, false)]
    [InlineData(10, 3, 11, 3, true, false)]
    [InlineData(10, 3, 10, 4, true, true)]
    [InlineData(11, 4, 11, 4, true, false)]
    public void ConflictRequiresDirtyChangesAgainstNewerTerms(
        long baseObjectRevision,
        int baseTermsVersion,
        long currentObjectRevision,
        int currentTermsVersion,
        bool hasLocalChanges,
        bool expectedConflict)
    {
        Assert.Equal(
            expectedConflict,
            CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
                new CompanyCommissionTermsRevisionBase(
                    new CompanyRecordRevision(baseObjectRevision),
                    baseTermsVersion),
                new CompanyRecordRevision(currentObjectRevision),
                currentTermsVersion,
                hasLocalChanges));
    }
}
