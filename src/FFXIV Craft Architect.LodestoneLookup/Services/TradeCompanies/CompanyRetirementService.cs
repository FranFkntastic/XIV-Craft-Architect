using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using Microsoft.Data.Sqlite;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed record CompanyRetirementObject(
    string ProfileId,
    string Collection,
    string ObjectId,
    long Revision);

public sealed record CompanyRetirementSatellites(
    CompanyMembershipFootprint Membership,
    int ActiveBriefs,
    int ActiveCapabilities,
    int OpenParticipantBootstraps,
    bool DiscordInstallation,
    int ActiveDiscordPublications,
    int NotificationRoutes,
    int PendingNotifications,
    int ClaimContacts,
    int PendingClaimContacts);

public sealed record CompanyRetirementOutcome(
    Guid CompanyId,
    string Status,
    IReadOnlyList<string> RefusalReasons,
    IReadOnlyList<CompanyRetirementObject> Tombstones,
    CompanyRetirementSatellites? Satellites,
    IReadOnlyList<string> Notes);

public sealed record CompanyRetirementReport(
    bool DryRun,
    bool Applied,
    IReadOnlyList<string> Backups,
    IReadOnlyList<CompanyRetirementOutcome> Companies);

// Operator-run retirement of stale company containers. Every requested company is
// planned first; if any one of them still has a live reference the whole run refuses
// and nothing is mutated. Applying tombstones the company row, its crafters and its
// company-scoped records through the revision path (marked retired so stale browser
// copies cannot restore them) and closes the company's satellite state: memberships,
// invitations, crafter bindings, public briefs and capabilities, Discord routes,
// notifications, claim contacts and publications, and participant bootstraps.
public sealed class CompanyRetirementService(
    ProfileHostOptions profileOptions,
    TradeMembershipOptions membershipOptions,
    CommissionBriefOptions briefOptions,
    DiscordCommissionOptions discordOptions,
    DiscordIdentityOptions identityOptions,
    SqliteProfileHostStore profiles,
    SqliteMembershipStore memberships,
    SqliteDiscordCollaborationStore collaboration,
    ILogger<CompanyRetirementService> logger)
{
    private const string CompanyCollectionPrefix = "tradeCompany.";
    private static readonly JsonSerializerOptions ReadOptions = ProfileSyncJson.CreateOptions();
    private static readonly int[] ActivePublicationStates =
    [
        (int)DiscordPublicationState.Open,
        (int)DiscordPublicationState.Assigned,
        (int)DiscordPublicationState.TestFixture
    ];

    private sealed record CompanyPlan(
        CompanyId CompanyId,
        Guid ActorProfileId,
        IReadOnlyList<CompanyRetirementObject> Tombstones,
        IReadOnlyList<Guid> CrafterIds,
        IReadOnlyList<string> Refusals,
        IReadOnlyList<string> Notes,
        CompanyRetirementSatellites Satellites,
        bool AlreadyRetired);

    public async Task<CompanyRetirementReport> RunAsync(
        IReadOnlyList<CompanyId> companyIds,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (companyIds.Count == 0)
        {
            throw new ArgumentException("At least one company ID is required.", nameof(companyIds));
        }

        var requested = companyIds.Distinct().ToArray();
        var live = await profiles.LoadAllLiveObjectsAsync(cancellationToken);
        var plans = new List<CompanyPlan>();
        foreach (var companyId in requested)
        {
            plans.Add(await PlanAsync(companyId, live, cancellationToken));
        }

        // References between companies retired together do not block each other.
        var retiring = plans
            .SelectMany(plan => plan.Tombstones)
            .Select(item => (item.ProfileId, item.Collection, item.ObjectId))
            .ToHashSet();
        var survivors = live
            .Where(item => !retiring.Contains((item.ProfileId, item.Collection, item.ObjectId)))
            .ToArray();
        plans = plans
            .Select(plan => plan with
            {
                Refusals = plan.Refusals.Concat(FindBlockingReferences(plan, survivors)).ToArray()
            })
            .ToList();

        var refused = plans.Any(plan => plan.Refusals.Count > 0);
        if (dryRun || refused)
        {
            return new CompanyRetirementReport(
                dryRun,
                Applied: false,
                [],
                plans.Select(plan => ToOutcome(
                        plan,
                        plan.Refusals.Count > 0 ? "refused" :
                        plan.AlreadyRetired ? "already_retired" :
                        refused ? "not_applied" : "planned"))
                    .ToArray());
        }

        var backups = new List<string>();
        {
            foreach (var path in new[]
                     {
                         profileOptions.DatabasePath,
                         membershipOptions.DatabasePath,
                         briefOptions.DatabasePath,
                         discordOptions.DatabasePath,
                         identityOptions.DatabasePath
                     }
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (await SqliteDatabaseBackup.CreateAsync(path, "pre-company-retirement", cancellationToken)
                    is { } backup)
                {
                    backups.Add(backup);
                }
            }
        }

        var outcomes = new List<CompanyRetirementOutcome>();
        foreach (var plan in plans)
        {
            // Already-retired companies still get the idempotent satellite sweep, which
            // finishes Discord cleanup that was waiting on withdrawn posts to drain.
            await ApplyAsync(plan, backups, cancellationToken);
            outcomes.Add(ToOutcome(plan, plan.AlreadyRetired ? "already_retired" : "retired"));
        }

        return new CompanyRetirementReport(dryRun, Applied: true, backups, outcomes);
    }

    private async Task<CompanyPlan> PlanAsync(
        CompanyId companyId,
        IReadOnlyList<HostedSyncObject> live,
        CancellationToken cancellationToken)
    {
        var id = companyId.Value.ToString("D");
        var tombstones = new List<CompanyRetirementObject>();
        var crafterIds = new List<Guid>();
        var notes = new List<string>();
        Guid? hostProfileId = null;

        foreach (var item in live)
        {
            if (item.Collection == ProfileSyncCollections.TradeCompanyProfiles &&
                string.Equals(item.ObjectId, id, StringComparison.OrdinalIgnoreCase))
            {
                tombstones.Add(ToObject(item));
                hostProfileId ??= Guid.TryParse(item.ProfileId, out var host) ? host : null;
            }
            else if (item.Collection == ProfileSyncCollections.TradeCrafters &&
                     TryRead<TradeCrafterProfile>(item.PayloadJson) is { } crafter &&
                     crafter.CompanyProfileId == companyId.Value)
            {
                tombstones.Add(ToObject(item));
                if (crafter.Id != Guid.Empty && !crafterIds.Contains(crafter.Id))
                {
                    crafterIds.Add(crafter.Id);
                }
            }
            else if (item.Collection.StartsWith(CompanyCollectionPrefix, StringComparison.Ordinal) &&
                     (item.ObjectId.StartsWith(id + ":", StringComparison.OrdinalIgnoreCase) ||
                      OwnedByCompany(item.PayloadJson, id)))
            {
                tombstones.Add(ToObject(item));
            }
        }

        var satellites = await LoadSatellitesAsync(companyId, cancellationToken);
        var refusals = new List<string>();
        var alreadyRetired = false;
        if (tombstones.Count == 0)
        {
            if (satellites.Membership.AlreadyRetired)
            {
                alreadyRetired = true;
            }
            else
            {
                refusals.Add("company_not_found: no live company row, crafter or company record carries this ID.");
            }
        }

        foreach (var (profileId, archived) in await LoadArchivedOrdersAsync(cancellationToken))
        {
            if (archived.PayloadJson.Contains(id, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"deep-archived order {profileId}/{archived.ObjectId} names this company; archives are history and stay untouched.");
            }
        }

        return new CompanyPlan(
            companyId,
            hostProfileId ?? companyId.Value,
            tombstones
                .OrderBy(item => item.Collection == ProfileSyncCollections.TradeCompanyProfiles)
                .ThenBy(item => item.Collection, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectId, StringComparer.Ordinal)
                .ToArray(),
            crafterIds,
            refusals,
            notes,
            satellites,
            alreadyRetired);
    }

    private static IEnumerable<string> FindBlockingReferences(
        CompanyPlan plan,
        IReadOnlyList<HostedSyncObject> survivors)
    {
        var id = plan.CompanyId.Value.ToString("D");
        foreach (var item in survivors)
        {
            var location = $"{item.Collection} {item.ProfileId}/{item.ObjectId}";
            if (item.Collection == ProfileSyncCollections.TradeOrders)
            {
                var order = TryRead<TradeOrder>(item.PayloadJson);
                if (order?.CompanyCommission?.CompanyId == plan.CompanyId &&
                    order.Status is not (TradeOrderStatus.Completed or TradeOrderStatus.Canceled))
                {
                    yield return order.CompanyCommission.ActiveClaim == null
                        ? $"open_commission: {location} is an open commission of this company."
                        : $"claimed_commission: {location} is a claimed commission of this company.";
                }
                else if (order?.CompanyProfileId == plan.CompanyId.Value ||
                         order?.CompanyCommission?.CompanyId == plan.CompanyId ||
                         item.PayloadJson.Contains(id, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"live_order: {location} references this company.";
                }
            }
            else if (item.Collection is ProfileSyncCollections.Plans or ProfileSyncCollections.TradePayrollDrafts &&
                     item.PayloadJson.Contains(id, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"live_{(item.Collection == ProfileSyncCollections.Plans ? "plan" : "payroll")}: {location} references this company.";
            }

            foreach (var crafterId in plan.CrafterIds)
            {
                if (item.PayloadJson.Contains(crafterId.ToString("D"), StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"crafter_referenced: {location} references crafter {crafterId:D}.";
                }
            }
        }
    }

    private async Task ApplyAsync(
        CompanyPlan plan,
        IReadOnlyList<string> backups,
        CancellationToken cancellationToken)
    {
        var companyId = plan.CompanyId;
        foreach (var item in plan.Tombstones)
        {
            var retired = await profiles.RetireObjectAsync(
                item.ProfileId,
                item.Collection,
                item.ObjectId,
                item.Revision,
                companyId,
                cancellationToken);
            if (!retired.Success)
            {
                throw new InvalidOperationException(
                    $"{item.Collection} {item.ProfileId}/{item.ObjectId} changed during retirement; rerun the command to finish.");
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = companyId.Value.ToString("D");
        await ExecuteAsync(briefOptions.DatabasePath, "commission_briefs",
            "UPDATE commission_briefs SET revoked_at_utc = $now WHERE lower(company_id) = $companyId AND revoked_at_utc IS NULL;",
            id, now, cancellationToken);
        await ExecuteAsync(briefOptions.DatabasePath, "company_commission_capabilities",
            "UPDATE company_commission_capabilities SET revoked_at_utc = $now WHERE lower(company_id) = $companyId AND revoked_at_utc IS NULL;",
            id, now, cancellationToken);
        await ExecuteAsync(identityOptions.DatabasePath, "discord_participant_bootstraps",
            "UPDATE discord_participant_bootstraps SET expires_at_utc = $now WHERE lower(company_id) = $companyId AND redeemed_at_utc IS NULL AND expires_at_utc > $now;",
            id, now, cancellationToken);

        // Live Discord posts are withdrawn through the outbox so the messages are removed.
        foreach (var publicationId in await LoadActivePublicationIdsAsync(id, cancellationToken))
        {
            var publication = await collaboration.LoadPublicationAsync(publicationId, cancellationToken);
            if (publication == null || !ActivePublicationStates.Contains((int)publication.State))
            {
                continue;
            }

            await collaboration.EnqueueProjectionAsync(
                publication.PublicationId,
                DiscordPublicationState.Suppressed,
                checked(publication.DesiredProjectionRevision + 1),
                "{}",
                DateTimeOffset.UtcNow,
                cancellationToken);
        }

        foreach (var (table, sql) in new[]
                 {
                     ("discord_notification_routes", "DELETE FROM discord_notification_routes WHERE lower(company_id) = $companyId;"),
                     ("discord_notification_outbox", "DELETE FROM discord_notification_outbox WHERE lower(company_id) = $companyId AND state IN (0, 2);"),
                     ("discord_claim_contacts", "DELETE FROM discord_claim_contacts WHERE lower(company_id) = $companyId;"),
                     ("discord_pending_claim_contacts", "DELETE FROM discord_pending_claim_contacts WHERE lower(company_id) = $companyId;")
                 })
        {
            await ExecuteAsync(discordOptions.DatabasePath, table, sql, id, now, cancellationToken);
        }

        // The installation stays until withdrawn posts have drained; a rerun removes it.
        var installationCleared = await PendingDiscordWorkAsync(id, cancellationToken) == 0 &&
            await ExecuteAsync(discordOptions.DatabasePath, "discord_company_installations",
                "DELETE FROM discord_company_installations WHERE lower(company_id) = $companyId;",
                id, now, cancellationToken) >= 0;

        var details = JsonSerializer.Serialize(new
        {
            tombstones = plan.Tombstones,
            satellites = plan.Satellites,
            discordInstallationCleared = installationCleared,
            backups,
            notes = plan.Notes
        });
        await memberships.RetireCompanyAsync(
            companyId,
            plan.ActorProfileId,
            "profile-host retire-company",
            details,
            cancellationToken);
        logger.LogWarning(
            "Company {CompanyId} retired: {Tombstones} records tombstoned; Discord installation cleared: {InstallationCleared}.",
            companyId.Value,
            plan.Tombstones.Count,
            installationCleared);
    }

    private async Task<CompanyRetirementSatellites> LoadSatellitesAsync(
        CompanyId companyId,
        CancellationToken cancellationToken)
    {
        var id = companyId.Value.ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");
        return new CompanyRetirementSatellites(
            await memberships.LoadCompanyFootprintAsync(companyId, cancellationToken),
            await CountAsync(briefOptions.DatabasePath, "commission_briefs",
                "SELECT count(*) FROM commission_briefs WHERE lower(company_id) = $companyId AND revoked_at_utc IS NULL;", id, now, cancellationToken),
            await CountAsync(briefOptions.DatabasePath, "company_commission_capabilities",
                "SELECT count(*) FROM company_commission_capabilities WHERE lower(company_id) = $companyId AND revoked_at_utc IS NULL;", id, now, cancellationToken),
            await CountAsync(identityOptions.DatabasePath, "discord_participant_bootstraps",
                "SELECT count(*) FROM discord_participant_bootstraps WHERE lower(company_id) = $companyId AND redeemed_at_utc IS NULL AND expires_at_utc > $now;", id, now, cancellationToken),
            await CountAsync(discordOptions.DatabasePath, "discord_company_installations",
                "SELECT count(*) FROM discord_company_installations WHERE lower(company_id) = $companyId;", id, now, cancellationToken) > 0,
            (await LoadActivePublicationIdsAsync(id, cancellationToken)).Count,
            await CountAsync(discordOptions.DatabasePath, "discord_notification_routes",
                "SELECT count(*) FROM discord_notification_routes WHERE lower(company_id) = $companyId;", id, now, cancellationToken),
            await CountAsync(discordOptions.DatabasePath, "discord_notification_outbox",
                "SELECT count(*) FROM discord_notification_outbox WHERE lower(company_id) = $companyId AND state IN (0, 2);", id, now, cancellationToken),
            await CountAsync(discordOptions.DatabasePath, "discord_claim_contacts",
                "SELECT count(*) FROM discord_claim_contacts WHERE lower(company_id) = $companyId;", id, now, cancellationToken),
            await CountAsync(discordOptions.DatabasePath, "discord_pending_claim_contacts",
                "SELECT count(*) FROM discord_pending_claim_contacts WHERE lower(company_id) = $companyId;", id, now, cancellationToken));
    }

    private async Task<IReadOnlyList<Guid>> LoadActivePublicationIdsAsync(
        string companyId,
        CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var connection = await OpenIfTableExistsAsync(
            discordOptions.DatabasePath,
            "discord_publications",
            cancellationToken);
        if (connection == null)
        {
            return ids;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT publication_id FROM discord_publications
            WHERE lower(company_id) = $companyId AND state IN ({string.Join(", ", ActivePublicationStates)});
            """;
        command.Parameters.AddWithValue("$companyId", companyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (Guid.TryParse(reader.GetString(0), out var publicationId))
            {
                ids.Add(publicationId);
            }
        }

        return ids;
    }

    private Task<int> PendingDiscordWorkAsync(string companyId, CancellationToken cancellationToken) =>
        CountAsync(
            discordOptions.DatabasePath,
            "discord_outbox",
            """
            SELECT count(*) FROM discord_outbox o
            INNER JOIN discord_publications p ON p.publication_id = o.publication_id
            WHERE lower(p.company_id) = $companyId AND o.state IN (0, 1, 2);
            """,
            companyId,
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);

    private async Task<List<(string ProfileId, ProfileSyncObjectEnvelope Order)>> LoadArchivedOrdersAsync(
        CancellationToken cancellationToken)
    {
        var archived = new List<(string, ProfileSyncObjectEnvelope)>();
        foreach (var profileId in await profiles.LoadActiveProfileIdsAsync(cancellationToken))
        {
            foreach (var order in await profiles.LoadAllDeepArchivedOrdersAsync(profileId, cancellationToken))
            {
                archived.Add((profileId, order));
            }
        }

        return archived;
    }

    private static async Task<int> CountAsync(
        string databasePath,
        string table,
        string sql,
        string companyId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenIfTableExistsAsync(databasePath, table, cancellationToken);
        if (connection == null)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$companyId", companyId);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> ExecuteAsync(
        string databasePath,
        string table,
        string sql,
        string companyId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenIfTableExistsAsync(databasePath, table, cancellationToken);
        if (connection == null)
        {
            return 0;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$companyId", companyId);
        command.Parameters.AddWithValue("$now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Satellite stores create their tables lazily; a store that never ran has nothing
    // to retire, and the maintenance path must never create its database.
    private static async Task<SqliteConnection?> OpenIfTableExistsAsync(
        string databasePath,
        string table,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(databasePath);
        if (!File.Exists(path))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table;";
        probe.Parameters.AddWithValue("$table", table);
        if (await probe.ExecuteScalarAsync(cancellationToken) != null)
        {
            return connection;
        }

        await connection.DisposeAsync();
        return null;
    }

    // A company-scoped record belongs to this company only when its own root
    // companyId says so; a mere mention elsewhere in the payload never qualifies.
    private static bool OwnedByCompany(string payloadJson, string companyId)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(
                payloadJson,
                new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true });
            var value = node?["companyId"];
            if (value is System.Text.Json.Nodes.JsonObject wrapped)
            {
                value = wrapped["value"];
            }

            return value is System.Text.Json.Nodes.JsonValue scalar &&
                scalar.TryGetValue<string>(out var text) &&
                string.Equals(text, companyId, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static CompanyRetirementOutcome ToOutcome(CompanyPlan plan, string status) =>
        new(plan.CompanyId.Value, status, plan.Refusals, plan.Tombstones, plan.Satellites, plan.Notes);

    private static CompanyRetirementObject ToObject(HostedSyncObject item) =>
        new(item.ProfileId, item.Collection, item.ObjectId, item.Revision);

    private static T? TryRead<T>(string payloadJson)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payloadJson, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
