using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;

namespace FFXIV_Craft_Architect.ContractTests;

// One person must map to exactly one workforce row: a silent duplicate or a dropped person
// would misreport company access and assignment history to the commissioner.
public sealed class CompanyWorkforceProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BoundMemberAndRecordMergeIntoOneRowAndEveryPersonAppearsOnce()
    {
        var owner = Guid.NewGuid();
        var crafterMember = Guid.NewGuid();
        var requester = Guid.NewGuid();
        var formerMember = Guid.NewGuid();
        var boundRecord = Crafter("Old Name");
        var contractor = Crafter("Wei Ning");
        var invitedRecord = Crafter("Invitee");
        var formerRecord = Crafter("Former");
        var unsyncedCandidate = Guid.NewGuid();
        var openInvitation = Invitation(null, "active");
        var orders = new[]
        {
            Order(boundRecord.Id, TradeOrderStatus.InProgress),
            Order(crafterMember, TradeOrderStatus.Assigned),
            Order(crafterMember, TradeOrderStatus.Completed),
            Order(contractor.Id, TradeOrderStatus.Completed),
            Order(formerMember, TradeOrderStatus.Assigned)
        };
        var hosted = new CompanyWorkforceHostedFacts(
            Members:
            [
                Member(owner, "Frantastic", "owner", "active"),
                Member(crafterMember, "They", "crafter", "active"),
                // A duplicated projection under a newer display name is still the same person.
                Member(crafterMember, "They (renamed)", "crafter", "active"),
                Member(requester, "Requester", "crafter", "pending"),
                Member(formerMember, "Gone", "crafter", "revoked")
            ],
            Requests: [new CompanyMembership("c", requester, "crafter", "pending", Now, null, null, "hi", false)],
            Invitations: [Invitation(invitedRecord.Id, "active"), openInvitation, Invitation(contractor.Id, "revoked")],
            Migration: new LegacyCrafterMigration(
                [
                    Candidate(boundRecord), Candidate(contractor), Candidate(invitedRecord), Candidate(formerRecord),
                    new LegacyCrafterCandidate(unsyncedCandidate, "Host only", "Siren", null)
                ],
                [Binding(boundRecord.Id, crafterMember), Binding(formerRecord.Id, formerMember)]));

        var rows = CompanyWorkforceProjection.Build([boundRecord, contractor, invitedRecord, formerRecord], orders, hosted);

        Assert.Equal(rows.Count, rows.Select(row => row.Key).Distinct().Count());
        Assert.Equal(7, rows.Count);

        var member = Assert.Single(rows, row => row.AccountProfileId == crafterMember);
        Assert.Equal(CompanyWorkforceRelationship.Crafter, member.Relationship);
        Assert.Equal([boundRecord.Id], member.RecordIds);
        Assert.Equal(2, member.ActiveOrders.Count);
        Assert.Equal(3, member.CommissionCount);
        Assert.DoesNotContain(rows, row => row.AccountProfileId == null && row.RecordIds.Contains(boundRecord.Id));

        Assert.Equal(CompanyWorkforceRelationship.Owner, Assert.Single(rows, row => row.AccountProfileId == owner).Relationship);
        var request = Assert.Single(rows, row => row.AccountProfileId == requester);
        Assert.Equal(CompanyWorkforceRelationship.Requested, request.Relationship);
        Assert.NotNull(request.Request);
        Assert.DoesNotContain(rows, row => row.AccountProfileId == formerMember);

        var wei = Assert.Single(rows, row => row.RecordIds.Contains(contractor.Id));
        Assert.Equal(CompanyWorkforceRelationship.Contractor, wei.Relationship);
        Assert.Equal(CompanyWorkforceAccess.None, wei.Access);
        Assert.Empty(wei.ActiveOrders);
        Assert.Equal(1, wei.CommissionCount);

        var invited = Assert.Single(rows, row => row.RecordIds.Contains(invitedRecord.Id));
        Assert.Equal(CompanyWorkforceRelationship.Invited, invited.Relationship);
        Assert.False(invited.IsMemberSide);

        // A record bound to a former member stays a contractor and keeps that account's work.
        var former = Assert.Single(rows, row => row.RecordIds.Contains(formerRecord.Id));
        Assert.Equal(CompanyWorkforceRelationship.Contractor, former.Relationship);
        Assert.Single(former.ActiveOrders);

        Assert.Single(rows, row => row.RecordIds.Contains(unsyncedCandidate));
        // An invitation not tied to a record is not a person; it lives only in the Invite drawer.
        Assert.DoesNotContain(rows, row => row.Invitation?.InvitationId == openInvitation.InvitationId);
    }

    [Fact]
    public void ViewersWithoutAdminDataSeeHubRosterAndRecordsOnly()
    {
        var account = Guid.NewGuid();
        var record = Crafter("Contractor");
        var hosted = new CompanyWorkforceHostedFacts(
            HubRoster: [new CompanyHubRosterMember(account, "Member", "operator"), new CompanyHubRosterMember(account, "Member", "operator")]);

        var rows = CompanyWorkforceProjection.Build([record], [], hosted);

        Assert.Equal(2, rows.Count);
        Assert.Equal(CompanyWorkforceRelationship.Operator, Assert.Single(rows, row => row.AccountProfileId == account).Relationship);
        Assert.Single(rows, row => row.RecordIds.Contains(record.Id));
        Assert.Single(CompanyWorkforceProjection.Build([record], [], null));
    }

    [Fact]
    public void MemberReadableBindingsFoldARecordIntoTheMemberRowForNonAdministrators()
    {
        var account = Guid.NewGuid();
        var record = Crafter("Old Name");
        var hosted = new CompanyWorkforceHostedFacts(
            HubRoster: [new CompanyHubRosterMember(account, "Member", "crafter")],
            Bindings: [new CompanyCrafterBinding(record.Id, account)]);

        var row = Assert.Single(CompanyWorkforceProjection.Build([record], [Order(record.Id, TradeOrderStatus.Assigned)], hosted));

        Assert.Equal(account, row.AccountProfileId);
        Assert.Equal([record.Id], row.RecordIds);
        Assert.Single(row.ActiveOrders);
    }

    private static TradeCrafterProfile Crafter(string name) => new()
    {
        Id = Guid.NewGuid(),
        CompanyProfileId = Guid.NewGuid(),
        DisplayName = name,
        WorldName = "Siren",
        DataCenter = "Aether"
    };

    private static TradeOrder Order(Guid assignee, TradeOrderStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Order",
        Status = status,
        AssignedCrafterId = assignee
    };

    private static CompanyMember Member(Guid id, string name, string role, string state) =>
        new(id, name, role, state, Now, Now, true);

    private static CompanyMembershipInvitation Invitation(Guid? recordId, string state) =>
        new(Guid.NewGuid(), "c", "Company", recordId, recordId == null ? null : "Record", Now, Now.AddDays(7), state, null);

    private static LegacyCrafterCandidate Candidate(TradeCrafterProfile crafter) =>
        new(crafter.Id, crafter.DisplayName, crafter.WorldName, null);

    private static LegacyCrafterBinding Binding(Guid recordId, Guid accountId) =>
        new(recordId, accountId, "operator", Now);
}
