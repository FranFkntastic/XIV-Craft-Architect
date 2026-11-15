using FFXIV_Craft_Architect.Core.Models;

namespace FFXIV_Craft_Architect.Web.Services.TradeCompany;

public enum CompanyWorkforceRelationship
{
    Owner,
    Operator,
    Crafter,
    Requested,
    Invited,
    Contractor
}

public enum CompanyWorkforceAccess
{
    Active,
    Pending,
    None
}

public enum CompanyWorkforceFilter
{
    Everyone,
    Members,
    Contractors
}

/// <summary>
/// Hosted membership facts available to the viewer. Administrative collections stay null
/// for viewers who may not load them (crafter-role members, local-only companies).
/// </summary>
public sealed record CompanyWorkforceHostedFacts(
    IReadOnlyList<CompanyMember>? Members = null,
    IReadOnlyList<CompanyHubRosterMember>? HubRoster = null,
    IReadOnlyList<CompanyMembership>? Requests = null,
    IReadOnlyList<CompanyMembershipInvitation>? Invitations = null,
    LegacyCrafterMigration? Migration = null);

public sealed record CompanyWorkforceRow(
    string Key,
    string DisplayName,
    string? Secondary,
    CompanyWorkforceRelationship Relationship,
    CompanyWorkforceAccess Access,
    Guid? AccountProfileId,
    CompanyMember? Member,
    CompanyMembership? Request,
    CompanyMembershipInvitation? Invitation,
    IReadOnlyList<Guid> RecordIds,
    IReadOnlyList<TradeOrder> ActiveOrders,
    int CommissionCount,
    string? AvatarUrl)
{
    public bool HasCharacterProfile => RecordIds.Count > 0;

    // Account-backed rows and bare invitations are prospective or actual members;
    // every other row is a company-owned contractor record.
    public bool IsMemberSide => AccountProfileId.HasValue || (Relationship == CompanyWorkforceRelationship.Invited && RecordIds.Count == 0);

    public bool Matches(CompanyWorkforceFilter filter) => filter switch
    {
        CompanyWorkforceFilter.Members => IsMemberSide,
        CompanyWorkforceFilter.Contractors => !IsMemberSide,
        _ => true
    };
}

/// <summary>
/// Projects authenticated memberships, company crafter records, account bindings and
/// invitations into one row per person. Membership identity is the stable account profile id;
/// a crafter record bound to an active or pending member folds into that member's row.
/// </summary>
public static class CompanyWorkforceProjection
{
    public static IReadOnlyList<CompanyWorkforceRow> Build(
        IReadOnlyList<TradeCrafterProfile> roster,
        IReadOnlyList<TradeOrder> orders,
        CompanyWorkforceHostedFacts? hosted)
    {
        hosted ??= new CompanyWorkforceHostedFacts();
        var bindings = hosted.Migration?.Bindings ?? [];
        var candidates = hosted.Migration?.LegacyCrafters ?? [];
        var requests = hosted.Requests ?? [];
        var activeInvitations = (hosted.Invitations ?? [])
            .Where(invitation => invitation.State == "active")
            .ToArray();
        var recordsById = roster
            .GroupBy(crafter => crafter.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var candidatesById = candidates
            .GroupBy(candidate => candidate.LegacyCrafterId)
            .ToDictionary(group => group.Key, group => group.First());

        var rows = new List<CompanyWorkforceRow>();
        var consumedRecords = new HashSet<Guid>();

        foreach (var person in ResolveMembers(hosted))
        {
            var recordIds = bindings
                .Where(binding => binding.AccountProfileId == person.AccountProfileId)
                .Select(binding => binding.LegacyCrafterId)
                .Distinct()
                .ToArray();
            consumedRecords.UnionWith(recordIds);
            var request = person.State == "pending"
                ? requests.FirstOrDefault(item => item.AccountProfileId == person.AccountProfileId)
                : null;
            var relationship = person.State == "pending"
                ? CompanyWorkforceRelationship.Requested
                : person.Role switch
                {
                    "owner" => CompanyWorkforceRelationship.Owner,
                    "operator" => CompanyWorkforceRelationship.Operator,
                    _ => CompanyWorkforceRelationship.Crafter
                };
            var assignmentIds = recordIds.Append(person.AccountProfileId).ToHashSet();
            rows.Add(new CompanyWorkforceRow(
                $"account:{person.AccountProfileId:N}",
                person.DisplayName,
                person.Member == null
                    ? null
                    : person.State == "pending"
                        ? $"Requested {person.Member.RequestedAtUtc.ToLocalTime():MMM d}"
                        : person.Member.DiscordLinked ? "Discord linked" : "Discord not linked",
                relationship,
                person.State == "pending" ? CompanyWorkforceAccess.Pending : CompanyWorkforceAccess.Active,
                person.AccountProfileId,
                person.Member,
                request,
                null,
                recordIds,
                ActiveOrdersFor(orders, assignmentIds),
                orders.Count(order => order.AssignedCrafterId is { } id && assignmentIds.Contains(id)),
                recordIds.Select(id => recordsById.GetValueOrDefault(id)?.LodestoneAvatarUrl)
                    .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))));
        }

        var contractorIds = recordsById.Keys
            .Concat(candidatesById.Keys)
            .Where(id => !consumedRecords.Contains(id))
            .Distinct();
        foreach (var recordId in contractorIds)
        {
            recordsById.TryGetValue(recordId, out var record);
            candidatesById.TryGetValue(recordId, out var candidate);
            var invitation = activeInvitations.FirstOrDefault(item => item.LegacyCrafterId == recordId);
            // Orders assigned to an account bound to this record (e.g. a former member) remain
            // this record's history, mirroring the roster assignment rule.
            var assignmentIds = bindings
                .Where(binding => binding.LegacyCrafterId == recordId)
                .Select(binding => binding.AccountProfileId)
                .Append(recordId)
                .ToHashSet();
            rows.Add(new CompanyWorkforceRow(
                $"record:{recordId:N}",
                record?.DisplayName ?? candidate?.DisplayName ?? "Unnamed crafter",
                record != null ? FormatLocation(record.WorldName, record.DataCenter) : FormatLocation(candidate?.WorldName, null),
                invitation != null ? CompanyWorkforceRelationship.Invited : CompanyWorkforceRelationship.Contractor,
                invitation != null ? CompanyWorkforceAccess.Pending : CompanyWorkforceAccess.None,
                null,
                null,
                null,
                invitation,
                [recordId],
                ActiveOrdersFor(orders, assignmentIds),
                orders.Count(order => order.AssignedCrafterId is { } id && assignmentIds.Contains(id)),
                string.IsNullOrWhiteSpace(record?.LodestoneAvatarUrl) ? null : record.LodestoneAvatarUrl));
        }

        var knownRecordIds = recordsById.Keys.Concat(candidatesById.Keys).ToHashSet();
        foreach (var invitation in activeInvitations.Where(item =>
                     item.LegacyCrafterId is not { } id || !knownRecordIds.Contains(id)))
        {
            rows.Add(new CompanyWorkforceRow(
                $"invitation:{invitation.InvitationId:N}",
                invitation.LegacyCrafterName ?? "Open invitation",
                $"Expires {invitation.ExpiresAtUtc.ToLocalTime():MMM d}",
                CompanyWorkforceRelationship.Invited,
                CompanyWorkforceAccess.Pending,
                null,
                null,
                null,
                invitation,
                [],
                [],
                0,
                null));
        }

        return rows
            .OrderBy(row => row.IsMemberSide ? 0 : 1)
            .ThenBy(row => row.Relationship)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string FormatLocation(string? world, string? dataCenter)
    {
        if (string.IsNullOrWhiteSpace(world) && string.IsNullOrWhiteSpace(dataCenter))
        {
            return "No world set";
        }

        if (string.IsNullOrWhiteSpace(world))
        {
            return dataCenter!;
        }

        return string.IsNullOrWhiteSpace(dataCenter) ? world : $"{world} · {dataCenter}";
    }

    private static IReadOnlyList<TradeOrder> ActiveOrdersFor(IReadOnlyList<TradeOrder> orders, HashSet<Guid> assignmentIds) => orders
        .Where(order => order.AssignedCrafterId is { } id &&
                        assignmentIds.Contains(id) &&
                        !TradeOrderStatusWorkflow.IsArchived(order.Status))
        .OrderByDescending(order => order.CommissionedAtUtc)
        .ToArray();

    private static IEnumerable<WorkforcePerson> ResolveMembers(CompanyWorkforceHostedFacts hosted)
    {
        if (hosted.Members != null)
        {
            // One row per stable account id; a duplicated projection never yields a second person.
            return hosted.Members
                .Where(member => member.State is "active" or "pending")
                .GroupBy(member => member.AccountProfileId)
                .Select(group => group.OrderBy(member => member.State == "active" ? 0 : 1).First())
                .Select(member => new WorkforcePerson(member.AccountProfileId, member.DisplayName, member.Role, member.State, member));
        }

        return (hosted.HubRoster ?? [])
            .GroupBy(member => member.AccountProfileId)
            .Select(group => group.First())
            .Select(member => new WorkforcePerson(member.AccountProfileId, member.DisplayName, member.Role, "active", null));
    }

    private sealed record WorkforcePerson(Guid AccountProfileId, string DisplayName, string Role, string State, CompanyMember? Member);
}
