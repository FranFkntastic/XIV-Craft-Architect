using System.Text.Json;
using System.Text.Json.Nodes;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public enum CompanyCrafterConsolidationMode
{
    Apply,
    DryRun,
    Disabled
}

public sealed class CompanyCrafterConsolidationOptions
{
    // Startup only reports; operators apply with `profile-host consolidate-crafters`
    // or by setting TradeCrafterConsolidation:Mode to Apply.
    public CompanyCrafterConsolidationMode Mode { get; set; } = CompanyCrafterConsolidationMode.DryRun;
}

public sealed record CompanyCrafterMergeOutcome(
    Guid CompanyId,
    string LodestoneCharacterId,
    Guid SurvivorCrafterId,
    IReadOnlyList<Guid> MergedCrafterIds,
    Guid? AccountProfileId,
    int OrdersRepointed,
    int PayrollDraftsRepointed,
    int ArchivedOrdersRepointed,
    int CrafterCopiesTombstoned,
    IReadOnlyList<string> FilledFields,
    string Status,
    string? Error = null);

public sealed record CompanyCrafterSkippedGroup(
    Guid CompanyId,
    string LodestoneCharacterId,
    IReadOnlyList<Guid> CrafterIds,
    string Reason);

public sealed record CompanyCrafterConsolidationReport(
    bool DryRun,
    IReadOnlyList<string> Backups,
    IReadOnlyList<CompanyCrafterMergeOutcome> Merges,
    IReadOnlyList<CompanyCrafterSkippedGroup> Skipped);

// One person is one crafter per company. Older clients created a second crafter row
// for the same exact Lodestone character; this repair folds each such group into one
// survivor, re-points every stored reference, and tombstones the rest through the
// normal revisioned sync path so browser copies converge. Only rows held by the
// company's hosting profile are read or rewritten: generic sync lets any profile
// upload a payload naming any company, so rows elsewhere are never trusted. Companies
// without a single host, and groups bound to different accounts, are left alone.
// Reruns find nothing left to merge.
public sealed class CompanyCrafterConsolidationService(
    ProfileHostOptions profileOptions,
    TradeMembershipOptions membershipOptions,
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    ILogger<CompanyCrafterConsolidationService> logger)
{
    public const string SkippedForConflictingAccounts = "bound_to_different_accounts";
    public const string SkippedForMissingHost = "host_profile_missing";
    public const string SkippedForAmbiguousHost = "host_profile_ambiguous";

    private static readonly JsonSerializerOptions ReadOptions = ProfileSyncJson.CreateOptions();
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    // Blank survivor fields are filled from the merged rows; populated ones never change.
    private static readonly string[] FillableScalarFields =
    [
        "displayName", "alias", "contactHandle", "discordHandle", "socialProfileUrl",
        "worldName", "dataCenter", "lodestoneProfileUrl", "lodestoneAvatarUrl",
        "lodestonePortraitUrl", "lodestoneFreeCompanyName", "lodestoneRace",
        "lodestoneClan", "lodestoneGender", "availabilityNotes", "paymentNotes",
        "operatorNotes", "lodestoneLastSyncedAtUtc"
    ];

    private readonly CompanyHostProfileResolver hosts = new(profiles);

    private sealed class RunState(bool dryRun)
    {
        public bool DryRun { get; } = dryRun;
        public List<string> Backups { get; } = [];
        public bool BackedUp { get; set; }
    }

    public async Task<CompanyCrafterConsolidationReport> RunAsync(
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var state = new RunState(dryRun);
        var merges = new List<CompanyCrafterMergeOutcome>();
        var skipped = new List<CompanyCrafterSkippedGroup>();
        var hostProfiles = await hosts.LoadAllAsync(cancellationToken);

        // Companies without a trusted host are only reported, never repaired.
        foreach (var (companyId, lodestone, crafterIds) in await FindUnhostedDuplicatesAsync(hostProfiles, cancellationToken))
        {
            var reason = hostProfiles.ContainsKey(companyId) ? SkippedForAmbiguousHost : SkippedForMissingHost;
            logger.LogWarning(
                "Crafter consolidation skipped company {CompanyId} Lodestone {LodestoneCharacterId}: {Reason} (candidate hosts {CandidateProfileIds}).",
                companyId,
                lodestone,
                reason,
                hostProfiles.TryGetValue(companyId, out var host) ? string.Join(",", host.CandidateProfileIds) : "none");
            skipped.Add(new CompanyCrafterSkippedGroup(companyId, lodestone, crafterIds, reason));
        }

        foreach (var host in hostProfiles.Values
                     .Where(item => item.Status == CompanyHostProfileStatus.Resolved)
                     .OrderBy(item => item.CompanyId.Value))
        {
            var companyId = host.CompanyId;
            var hostProfileId = host.ProfileId!;
            var groups = (await hosts.LoadCraftersAsync(hostProfileId, companyId, cancellationToken))
                .Select(item => (Crafter: item, Lodestone: LegacyCrafterAccountResolver.NormalizeLodestoneId(item.Crafter.LodestoneCharacterId)))
                .Where(item => item.Lodestone != null)
                .GroupBy(item => item.Lodestone!, item => item.Crafter, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToArray();
            if (groups.Length == 0)
            {
                continue;
            }

            var bindings = (await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken))
                .ToDictionary(item => item.LegacyCrafterId, item => item.AccountProfileId);
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var members = group
                    .OrderBy(item => item.Crafter.CreatedAtUtc)
                    .ThenBy(item => item.Crafter.Id)
                    .ToArray();
                var bound = members.Where(item => bindings.ContainsKey(item.Crafter.Id)).ToArray();
                var accounts = bound.Select(item => bindings[item.Crafter.Id]).Distinct().ToArray();
                if (accounts.Length > 1)
                {
                    logger.LogWarning(
                        "Crafter consolidation skipped company {CompanyId} Lodestone {LodestoneCharacterId}: crafters {CrafterIds} are bound to different accounts.",
                        companyId.Value,
                        group.Key,
                        string.Join(",", members.Select(item => item.Crafter.Id)));
                    skipped.Add(new CompanyCrafterSkippedGroup(
                        companyId.Value,
                        group.Key,
                        members.Select(item => item.Crafter.Id).ToArray(),
                        SkippedForConflictingAccounts));
                    continue;
                }

                var survivor = (bound.Length > 0 ? bound : members)[0];
                var losers = members.Where(item => item.Crafter.Id != survivor.Crafter.Id).ToArray();
                try
                {
                    merges.Add(await MergeAsync(
                        state,
                        companyId,
                        hostProfileId,
                        group.Key,
                        survivor,
                        losers,
                        accounts.Length == 1 ? accounts[0] : null,
                        cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(
                        exception,
                        "Crafter consolidation failed for company {CompanyId} Lodestone {LodestoneCharacterId}; the group stays unmerged and will be retried.",
                        companyId.Value,
                        group.Key);
                    merges.Add(new CompanyCrafterMergeOutcome(
                        companyId.Value,
                        group.Key,
                        survivor.Crafter.Id,
                        losers.Select(item => item.Crafter.Id).ToArray(),
                        accounts.Length == 1 ? accounts[0] : null,
                        0,
                        0,
                        0,
                        0,
                        [],
                        "failed",
                        exception.Message));
                }
            }
        }

        return new CompanyCrafterConsolidationReport(dryRun, state.Backups, merges, skipped);
    }

    private async Task<CompanyCrafterMergeOutcome> MergeAsync(
        RunState state,
        CompanyId companyId,
        string hostProfileId,
        string lodestone,
        HostedCompanyCrafter survivor,
        IReadOnlyList<HostedCompanyCrafter> losers,
        Guid? accountProfileId,
        CancellationToken cancellationToken)
    {
        var map = losers.ToDictionary(item => item.Crafter.Id, _ => survivor.Crafter.Id);
        var survivorName = string.IsNullOrWhiteSpace(survivor.Crafter.DisplayName)
            ? null
            : survivor.Crafter.DisplayName.Trim();
        var donors = losers.Select(item => item.PayloadJson).ToArray();

        // Plan every rewrite first so a dry run reports exactly what apply would change.
        // Reads are fresh per group so each merge plans against the revisions the
        // previous one wrote.
        var orders = await profiles.LoadProfileObjectsAsync(hostProfileId, ProfileSyncCollections.TradeOrders, cancellationToken);
        var drafts = await profiles.LoadProfileObjectsAsync(hostProfileId, ProfileSyncCollections.TradePayrollDrafts, cancellationToken);
        var archived = await profiles.LoadAllDeepArchivedOrdersAsync(hostProfileId, cancellationToken);

        var orderRewrites = orders
            .Select(item => (Item: item.Object, Payload: RewriteOrder(item.Object.PayloadJson, companyId, map)))
            .Where(item => item.Payload != null)
            .ToArray();
        var draftRewrites = drafts
            .Select(item => (Item: item.Object, Payload: RewriteDraft(item.Object.PayloadJson, companyId, map, survivorName)))
            .Where(item => item.Payload != null)
            .ToArray();
        var archiveRewrites = archived
            .Select(item => (Item: item, Payload: RewriteOrder(item.PayloadJson, companyId, map)))
            .Where(item => item.Payload != null)
            .ToArray();
        var fill = FillBlanks(survivor.PayloadJson, donors);

        if (state.DryRun)
        {
            logger.LogInformation(
                "Crafter consolidation dry run: company {CompanyId} Lodestone {LodestoneCharacterId} would keep {SurvivorId} and merge {LoserIds} ({Orders} orders, {Drafts} payroll drafts, {Archived} archived orders).",
                companyId.Value,
                lodestone,
                survivor.Crafter.Id,
                string.Join(",", map.Keys),
                orderRewrites.Length,
                draftRewrites.Length,
                archiveRewrites.Length);
            return new CompanyCrafterMergeOutcome(
                companyId.Value,
                lodestone,
                survivor.Crafter.Id,
                map.Keys.ToArray(),
                accountProfileId,
                orderRewrites.Length,
                draftRewrites.Length,
                archiveRewrites.Length,
                losers.Count,
                fill.Fields,
                "planned");
        }

        await EnsureBackupsAsync(state, cancellationToken);

        foreach (var (item, payload) in orderRewrites)
        {
            await PutAsync(hostProfileId, ProfileSyncCollections.TradeOrders, item.ObjectId, item.Revision, payload!, cancellationToken);
        }

        foreach (var (item, payload) in draftRewrites)
        {
            await PutAsync(hostProfileId, ProfileSyncCollections.TradePayrollDrafts, item.ObjectId, item.Revision, payload!, cancellationToken);
        }

        foreach (var (item, payload) in archiveRewrites)
        {
            if (!await profiles.ReplaceDeepArchivedOrderPayloadAsync(
                    hostProfileId,
                    item.ObjectId,
                    item.PayloadJson,
                    payload!,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Archived order {item.ObjectId} changed while its crafter was being re-pointed.");
            }
        }

        if (fill.Payload != null)
        {
            await PutAsync(
                hostProfileId,
                ProfileSyncCollections.TradeCrafters,
                survivor.Crafter.Id.ToString("D"),
                survivor.Revision,
                fill.Payload,
                cancellationToken);
        }

        var details = JsonSerializer.Serialize(new
        {
            survivorCrafterId = survivor.Crafter.Id,
            hostProfileId,
            ordersRepointed = orderRewrites.Length,
            payrollDraftsRepointed = draftRewrites.Length,
            archivedOrdersRepointed = archiveRewrites.Length,
            filledFields = fill.Fields
        });
        var mergedAt = DateTimeOffset.UtcNow;
        foreach (var loser in losers)
        {
            await memberships.RepointCrafterReferencesAsync(
                new CompanyCrafterMergeReceipt(
                    companyId,
                    loser.Crafter.Id,
                    survivor.Crafter.Id,
                    lodestone,
                    accountProfileId,
                    loser.PayloadJson,
                    details,
                    mergedAt),
                cancellationToken);
        }

        foreach (var loser in losers)
        {
            var deleted = await profiles.DeleteObjectAsync(
                hostProfileId,
                ProfileSyncCollections.TradeCrafters,
                loser.Crafter.Id.ToString("D"),
                loser.Revision,
                cancellationToken);
            if (!deleted.Success)
            {
                throw new InvalidOperationException(
                    $"Crafter {loser.Crafter.Id:D} changed before it could be tombstoned ({deleted.ErrorCode ?? "conflict"}).");
            }
        }

        logger.LogInformation(
            "Crafter consolidation merged company {CompanyId} Lodestone {LodestoneCharacterId}: kept {SurvivorId}, merged {LoserIds}, re-pointed {Orders} orders, {Drafts} payroll drafts and {Archived} archived orders.",
            companyId.Value,
            lodestone,
            survivor.Crafter.Id,
            string.Join(",", map.Keys),
            orderRewrites.Length,
            draftRewrites.Length,
            archiveRewrites.Length);
        return new CompanyCrafterMergeOutcome(
            companyId.Value,
            lodestone,
            survivor.Crafter.Id,
            map.Keys.ToArray(),
            accountProfileId,
            orderRewrites.Length,
            draftRewrites.Length,
            archiveRewrites.Length,
            losers.Count,
            fill.Fields,
            "merged");
    }

    private async Task EnsureBackupsAsync(RunState state, CancellationToken cancellationToken)
    {
        if (state.BackedUp)
        {
            return;
        }

        foreach (var path in new[] { profileOptions.DatabasePath, membershipOptions.DatabasePath })
        {
            if (await SqliteDatabaseBackup.CreateAsync(path, "pre-crafter-merge", cancellationToken) is { } backup)
            {
                state.Backups.Add(backup);
                logger.LogInformation("Crafter consolidation backed up {DatabasePath} to {BackupPath}.", path, backup);
            }
        }

        state.BackedUp = true;
    }

    private async Task PutAsync(
        string profileId,
        string collection,
        string objectId,
        long revision,
        string payload,
        CancellationToken cancellationToken)
    {
        var put = await profiles.PutObjectAsync(
            profileId,
            collection,
            objectId,
            payload,
            revision,
            cancellationToken);
        if (!put.Success)
        {
            throw new InvalidOperationException(
                $"{collection} object {objectId} could not be re-pointed ({put.ErrorCode ?? "conflict"}).");
        }
    }

    // Duplicate groups in companies that have no single host profile. These rows are
    // untrusted and only surface in the report so an operator can look at them.
    private async Task<IReadOnlyList<(Guid CompanyId, string Lodestone, IReadOnlyList<Guid> CrafterIds)>> FindUnhostedDuplicatesAsync(
        IReadOnlyDictionary<Guid, CompanyHostProfile> hostProfiles,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid CompanyId, Guid CrafterId, string Lodestone)>();
        foreach (var item in await profiles.LoadObjectsAsync(ProfileSyncCollections.TradeCrafters, cancellationToken))
        {
            TradeCrafterProfile? crafter;
            try
            {
                crafter = JsonSerializer.Deserialize<TradeCrafterProfile>(item.Object.PayloadJson, ReadOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (crafter == null ||
                crafter.Id == Guid.Empty ||
                crafter.CompanyProfileId == Guid.Empty ||
                hostProfiles.TryGetValue(crafter.CompanyProfileId, out var host) &&
                host.Status == CompanyHostProfileStatus.Resolved ||
                LegacyCrafterAccountResolver.NormalizeLodestoneId(crafter.LodestoneCharacterId) is not { } lodestone)
            {
                continue;
            }

            rows.Add((crafter.CompanyProfileId, crafter.Id, lodestone));
        }

        return rows
            .GroupBy(row => (row.CompanyId, row.Lodestone))
            .Select(group => (group.Key.CompanyId, group.Key.Lodestone,
                (IReadOnlyList<Guid>)group.Select(row => row.CrafterId).Distinct().Order().ToArray()))
            .Where(group => group.Item3.Count > 1)
            .OrderBy(group => group.CompanyId)
            .ThenBy(group => group.Lodestone, StringComparer.Ordinal)
            .ToArray();
    }

    // Returns the rewritten order payload, or null when the order is not this
    // company's or references none of the merged crafters.
    private static string? RewriteOrder(
        string payloadJson,
        CompanyId companyId,
        IReadOnlyDictionary<Guid, Guid> map)
    {
        TradeOrder? order;
        JsonObject? node;
        try
        {
            order = JsonSerializer.Deserialize<TradeOrder>(payloadJson, ReadOptions);
            node = JsonNode.Parse(payloadJson, NodeOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (order == null || node == null ||
            !(order.CompanyCommission?.CompanyId == companyId ||
              order.CompanyCommission == null && order.CompanyProfileId == companyId.Value))
        {
            return null;
        }

        var changed = Repoint(node, "assignedCrafterId", map);
        if (node["history"] is JsonArray history)
        {
            foreach (var entry in history.OfType<JsonObject>())
            {
                changed |= Repoint(entry, "crafterId", map);
            }
        }

        if (node["companyCommission"] is JsonObject commission)
        {
            var beforeUpgrade = commission.ToJsonString();
            FFXIV_Craft_Architect.Core.Services.TradeCompanyCommissionMigrationService
                .UpgradeStoredClaimIdentity(commission);
            changed |= beforeUpgrade != commission.ToJsonString();
            if (commission["activeClaim"] is JsonObject claim)
            {
                changed |= Repoint(claim, "crafterId", map);
                changed |= Repoint(claim, "claimantCharacterId", map);
            }

            if (commission["claimantCharacter"] is JsonObject character)
            {
                changed |= Repoint(character, "claimantCharacterId", map);
            }
        }

        return changed ? node.ToJsonString() : null;
    }

    private static string? RewriteDraft(
        string payloadJson,
        CompanyId companyId,
        IReadOnlyDictionary<Guid, Guid> map,
        string? survivorName)
    {
        TradePayrollWorkflowDraft? draft;
        JsonObject? node;
        try
        {
            draft = JsonSerializer.Deserialize<TradePayrollWorkflowDraft>(payloadJson, ReadOptions);
            node = JsonNode.Parse(payloadJson, NodeOptions) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (draft?.CompanyProfileId != companyId.Value || node == null ||
            !Repoint(node, "assignedCrafterId", map))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(survivorName))
        {
            node["assignedCrafterDisplayName"] = survivorName;
        }

        return node.ToJsonString();
    }

    private static (string? Payload, IReadOnlyList<string> Fields) FillBlanks(
        string survivorPayload,
        IReadOnlyList<string> donors)
    {
        if (JsonNode.Parse(survivorPayload, NodeOptions) is not JsonObject survivor)
        {
            return (null, []);
        }

        var donorNodes = donors
            .Select(payload => JsonNode.Parse(payload, NodeOptions))
            .OfType<JsonObject>()
            .ToArray();
        var filled = new List<string>();
        foreach (var field in FillableScalarFields)
        {
            if (!IsBlank(survivor[field]))
            {
                continue;
            }

            var donor = donorNodes.Select(item => item[field]).FirstOrDefault(value => !IsBlank(value));
            if (donor != null)
            {
                survivor[field] = donor.DeepClone();
                filled.Add(field);
            }
        }

        if (survivor["jobLevels"] is not JsonArray { Count: > 0 } &&
            donorNodes.Select(item => item["jobLevels"]).OfType<JsonArray>().FirstOrDefault(levels => levels.Count > 0)
                is { } jobLevels)
        {
            survivor["jobLevels"] = jobLevels.DeepClone();
            filled.Add("jobLevels");
        }

        if (filled.Count == 0)
        {
            return (null, []);
        }

        survivor["updatedAtUtc"] = DateTime.UtcNow.ToString("O");
        return (survivor.ToJsonString(), filled);
    }

    private static bool IsBlank(JsonNode? value) =>
        value == null ||
        value is JsonValue scalar &&
        scalar.TryGetValue<string>(out var text) &&
        string.IsNullOrWhiteSpace(text);

    private static bool Repoint(JsonObject node, string property, IReadOnlyDictionary<Guid, Guid> map)
    {
        if (node[property] is JsonValue value &&
            value.TryGetValue<string>(out var text) &&
            Guid.TryParse(text, out var crafterId) &&
            map.TryGetValue(crafterId, out var survivorId))
        {
            node[property] = survivorId.ToString("D");
            return true;
        }

        return false;
    }
}
