using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Pages;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionTermsRevisionConflictPolicyTests
{
    private static readonly CompanyCommissionTermsRevisionBase RevisionBase =
        new(new CompanyRecordRevision(10), TermsVersion: 3);

    [Theory]
    [InlineData(10, 3)]
    [InlineData(11, 3)]
    [InlineData(10, 4)]
    public void CleanBufferNeverConflicts(
        long currentObjectRevision,
        int currentTermsVersion)
    {
        Assert.False(CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
            RevisionBase,
            new CompanyRecordRevision(currentObjectRevision),
            currentTermsVersion,
            hasLocalChanges: false));
    }

    [Fact]
    public void DirtyBufferDoesNotConflictWithUnrelatedNewerOwnerRevision()
    {
        Assert.False(CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
            RevisionBase,
            new CompanyRecordRevision(11),
            currentTermsVersion: 3,
            hasLocalChanges: true));
    }

    [Fact]
    public void DirtyBufferConflictsWithNewerTermsVersion()
    {
        Assert.True(CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
            RevisionBase,
            new CompanyRecordRevision(10),
            currentTermsVersion: 4,
            hasLocalChanges: true));
    }

    [Fact]
    public void RebasedDirtyBufferDoesNotConflictAtAdvancedBase()
    {
        var rebased = new CompanyCommissionTermsRevisionBase(
            new CompanyRecordRevision(11),
            TermsVersion: 4);

        Assert.False(CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
            rebased,
            new CompanyRecordRevision(11),
            currentTermsVersion: 4,
            hasLocalChanges: true));
    }
}
