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
    public CompanyCrafterConsolidationMode Mode { get; set; } = CompanyCrafterConsolidationMode.Apply;
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
// normal revisioned sync path so browser copies converge. Groups whose rows are bound
// to different accounts are left alone. Reruns find nothing left to merge.
public sealed class CompanyCrafterConsolidationService(
    ProfileHostOptions profileOptions,
    TradeMembershipOptions membershipOptions,
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    ILogger<CompanyCrafterConsolidationService> logger)
{
    public const string SkippedForConflictingAccounts = "bound_to_different_accounts";

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

    private sealed record CrafterCopy(
        string ProfileId,
        string ObjectId,
        long Revision,
        string PayloadJson,
        TradeCrafterProfile Crafter);

    private sealed record CrafterIdentity(
        Guid CompanyId,
        Guid CrafterId,
        string? LodestoneCharacterId,
        DateTime CreatedAtUtc,
        IReadOnlyList<CrafterCopy> Copies);

    private sealed class RunState(bool dryRun)
    {
        public bool DryRun { get; } = dryRun;
        public List<string> Backups { get; } = [];
        public bool BackedUp { get; set; }
        public IReadOnlyList<HostedProfileObject>? Orders { get; set; }
        public IReadOnlyList<HostedProfileObject>? Drafts { get; set; }
        public List<(string ProfileId, ProfileSyncObjectEnvelope Order)>? ArchivedOrders { get; set; }
    }

    public async Task<CompanyCrafterConsolidationReport> RunAsync(
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var state = new RunState(dryRun);
        var merges = new List<CompanyCrafterMergeOutcome>();
        var skipped = new List<CompanyCrafterSkippedGroup>();
        var identities = await LoadCrafterIdentitiesAsync(cancellationToken);
        var groups = identities
            .Where(item => item.LodestoneCharacterId != null)
            .GroupBy(item => (item.CompanyId, Lodestone: item.LodestoneCharacterId!))
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key.CompanyId)
            .ThenBy(group => group.Key.Lodestone, StringComparer.Ordinal)
            .ToArray();
        var bindingsByCompany = new Dictionary<Guid, IReadOnlyDictionary<Guid, Guid>>();

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var companyId = new CompanyId(group.Key.CompanyId);
            if (!bindingsByCompany.TryGetValue(companyId.Value, out var bindings))
            {
                bindings = (await memberships.LoadCrafterBindingsAsync(companyId, cancellationToken))
                    .ToDictionary(item => item.LegacyCrafterId, item => item.AccountProfileId);
                bindingsByCompany[companyId.Value] = bindings;
            }

            var members = group
                .OrderBy(item => item.CreatedAtUtc)
                .ThenBy(item => item.CrafterId)
                .ToArray();
            var bound = members.Where(item => bindings.ContainsKey(item.CrafterId)).ToArray();
            var accounts = bound.Select(item => bindings[item.CrafterId]).Distinct().ToArray();
            if (accounts.Length > 1)
            {
                logger.LogWarning(
                    "Crafter consolidation skipped company {CompanyId} Lodestone {LodestoneCharacterId}: crafters {CrafterIds} are bound to different accounts.",
                    companyId.Value,
                    group.Key.Lodestone,
                    string.Join(",", members.Select(item => item.CrafterId)));
                skipped.Add(new CompanyCrafterSkippedGroup(
                    companyId.Value,
                    group.Key.Lodestone,
                    members.Select(item => item.CrafterId).ToArray(),
                    SkippedForConflictingAccounts));
                continue;
            }

            var survivor = (bound.Length > 0 ? bound : members)[0];
            var losers = members.Where(item => item.CrafterId != survivor.CrafterId).ToArray();
            try
            {
                merges.Add(await MergeAsync(
                    state,
                    companyId,
                    group.Key.Lodestone,
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
                    group.Key.Lodestone);
                merges.Add(new CompanyCrafterMergeOutcome(
                    companyId.Value,
                    group.Key.Lodestone,
                    survivor.CrafterId,
                    losers.Select(item => item.CrafterId).ToArray(),
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

        return new CompanyCrafterConsolidationReport(dryRun, state.Backups, merges, skipped);
    }

    private async Task<CompanyCrafterMergeOutcome> MergeAsync(
        RunState state,
        CompanyId companyId,
        string lodestone,
        CrafterIdentity survivor,
        IReadOnlyList<CrafterIdentity> losers,
        Guid? accountProfileId,
        CancellationToken cancellationToken)
    {
        var map = losers.ToDictionary(item => item.CrafterId, _ => survivor.CrafterId);
        var survivorName = survivor.Copies
            .Select(copy => copy.Crafter.DisplayName?.Trim())
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
        var donors = losers
            .SelectMany(item => item.Copies)
            .OrderBy(copy => copy.Crafter.CreatedAtUtc)
            .ThenBy(copy => copy.Crafter.Id)
            .ToArray();

        // Plan every rewrite first so a dry run reports exactly what apply would change.
        state.Orders ??= await profiles.LoadObjectsAsync(ProfileSyncCollections.TradeOrders, cancellationToken);
        state.Drafts ??= await profiles.LoadObjectsAsync(ProfileSyncCollections.TradePayrollDrafts, cancellationToken);
        if (state.ArchivedOrders == null)
        {
            state.ArchivedOrders = [];
            foreach (var profileId in await profiles.LoadActiveProfileIdsAsync(cancellationToken))
            {
                foreach (var archived in await profiles.LoadAllDeepArchivedOrdersAsync(profileId, cancellationToken))
                {
                    state.ArchivedOrders.Add((profileId, archived));
                }
            }
        }

        var orderRewrites = state.Orders
            .Select(item => (Item: item, Payload: RewriteOrder(item.Object.PayloadJson, companyId, map)))
            .Where(item => item.Payload != null)
            .ToArray();
        var draftRewrites = state.Drafts
            .Select(item => (Item: item, Payload: RewriteDraft(item.Object.PayloadJson, companyId, map, survivorName)))
            .Where(item => item.Payload != null)
            .ToArray();
        var archiveRewrites = state.ArchivedOrders
            .Select(item => (Item: item, Payload: RewriteOrder(item.Order.PayloadJson, companyId, map)))
            .Where(item => item.Payload != null)
            .ToArray();
        var survivorRewrites = survivor.Copies
            .Select(copy => (Copy: copy, Fill: FillBlanks(copy.PayloadJson, donors)))
            .Where(item => item.Fill.Payload != null)
            .ToArray();
        var filledFields = survivorRewrites
            .SelectMany(item => item.Fill.Fields)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var loserCopies = losers.SelectMany(item => item.Copies).ToArray();

        if (state.DryRun)
        {
            logger.LogInformation(
                "Crafter consolidation dry run: company {CompanyId} Lodestone {LodestoneCharacterId} would keep {SurvivorId} and merge {LoserIds} ({Orders} orders, {Drafts} payroll drafts, {Archived} archived orders).",
                companyId.Value,
                lodestone,
                survivor.CrafterId,
                string.Join(",", map.Keys),
                orderRewrites.Length,
                draftRewrites.Length,
                archiveRewrites.Length);
            return new CompanyCrafterMergeOutcome(
                companyId.Value,
                lodestone,
                survivor.CrafterId,
                map.Keys.ToArray(),
                accountProfileId,
                orderRewrites.Length,
                draftRewrites.Length,
                archiveRewrites.Length,
                loserCopies.Length,
                filledFields,
                "planned");
        }

        await EnsureBackupsAsync(state, cancellationToken);

        foreach (var (item, payload) in orderRewrites)
        {
            await PutAsync(item.ProfileId, ProfileSyncCollections.TradeOrders, item.Object, payload!, cancellationToken);
        }

        foreach (var (item, payload) in draftRewrites)
        {
            await PutAsync(item.ProfileId, ProfileSyncCollections.TradePayrollDrafts, item.Object, payload!, cancellationToken);
        }

        foreach (var ((profileId, archived), payload) in archiveRewrites)
        {
            if (!await profiles.ReplaceDeepArchivedOrderPayloadAsync(
                    profileId,
                    archived.ObjectId,
                    archived.PayloadJson,
                    payload!,
                    cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Archived order {archived.ObjectId} changed while its crafter was being re-pointed.");
            }
        }

        foreach (var (copy, fill) in survivorRewrites)
        {
            await PutAsync(
                copy.ProfileId,
                ProfileSyncCollections.TradeCrafters,
                copy.ObjectId,
                copy.Revision,
                fill.Payload!,
                cancellationToken);
        }

        var details = JsonSerializer.Serialize(new
        {
            survivorCrafterId = survivor.CrafterId,
            ordersRepointed = orderRewrites.Length,
            payrollDraftsRepointed = draftRewrites.Length,
            archivedOrdersRepointed = archiveRewrites.Length,
            filledFields
        });
        var mergedAt = DateTimeOffset.UtcNow;
        foreach (var loser in losers)
        {
            var loserPayload = loser.Copies
                .OrderByDescending(copy => copy.Crafter.UpdatedAtUtc)
                .ThenByDescending(copy => copy.Revision)
                .First()
                .PayloadJson;
            await memberships.RepointCrafterReferencesAsync(
                new CompanyCrafterMergeReceipt(
                    companyId,
                    loser.CrafterId,
                    survivor.CrafterId,
                    lodestone,
                    accountProfileId,
                    loserPayload,
                    details,
                    mergedAt),
                cancellationToken);
        }

        foreach (var copy in loserCopies)
        {
            var deleted = await profiles.DeleteObjectAsync(
                copy.ProfileId,
                ProfileSyncCollections.TradeCrafters,
                copy.ObjectId,
                copy.Revision,
                cancellationToken);
            if (!deleted.Success)
            {
                throw new InvalidOperationException(
                    $"Crafter {copy.ObjectId} changed before it could be tombstoned ({deleted.ErrorCode ?? "conflict"}).");
            }
        }

        // Later groups must plan against the revisions this merge just wrote.
        state.Orders = null;
        state.Drafts = null;
        state.ArchivedOrders = null;
        logger.LogInformation(
            "Crafter consolidation merged company {CompanyId} Lodestone {LodestoneCharacterId}: kept {SurvivorId}, merged {LoserIds}, re-pointed {Orders} orders, {Drafts} payroll drafts and {Archived} archived orders.",
            companyId.Value,
            lodestone,
            survivor.CrafterId,
            string.Join(",", map.Keys),
            orderRewrites.Length,
            draftRewrites.Length,
            archiveRewrites.Length);
        return new CompanyCrafterMergeOutcome(
            companyId.Value,
            lodestone,
            survivor.CrafterId,
            map.Keys.ToArray(),
            accountProfileId,
            orderRewrites.Length,
            draftRewrites.Length,
            archiveRewrites.Length,
            loserCopies.Length,
            filledFields,
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

    private Task PutAsync(
        string profileId,
        string collection,
        ProfileSyncObjectEnvelope current,
        string payload,
        CancellationToken cancellationToken) =>
        PutAsync(profileId, collection, current.ObjectId, current.Revision, payload, cancellationToken);

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

    private async Task<IReadOnlyList<CrafterIdentity>> LoadCrafterIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        var copies = new List<CrafterCopy>();
        foreach (var item in await profiles.LoadObjectsAsync(
                     ProfileSyncCollections.TradeCrafters,
                     cancellationToken))
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
                !string.Equals(item.Object.ObjectId, crafter.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            copies.Add(new CrafterCopy(
                item.ProfileId,
                item.Object.ObjectId,
                item.Object.Revision,
                item.Object.PayloadJson,
                crafter));
        }

        return copies
            .GroupBy(copy => (copy.Crafter.CompanyProfileId, copy.Crafter.Id))
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(copy => copy.Crafter.UpdatedAtUtc)
                    .ThenByDescending(copy => copy.Revision)
                    .First();
                return new CrafterIdentity(
                    group.Key.CompanyProfileId,
                    group.Key.Id,
                    LegacyCrafterAccountResolver.NormalizeLodestoneId(latest.Crafter.LodestoneCharacterId),
                    group.Min(copy => copy.Crafter.CreatedAtUtc),
                    group.ToArray());
            })
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
            if (commission["activeClaim"] is JsonObject claim)
            {
                changed |= Repoint(claim, "crafterId", map);
                changed |= Repoint(claim, "provisionalCrafterId", map);
            }

            if (commission["provisionalCrafter"] is JsonObject provisional)
            {
                changed |= Repoint(provisional, "provisionalCrafterId", map);
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
        IReadOnlyList<CrafterCopy> donors)
    {
        if (JsonNode.Parse(survivorPayload, NodeOptions) is not JsonObject survivor)
        {
            return (null, []);
        }

        var donorNodes = donors
            .Select(copy => JsonNode.Parse(copy.PayloadJson, NodeOptions))
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
