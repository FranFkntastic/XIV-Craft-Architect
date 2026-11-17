using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class ConfirmedClaimBindingContractTests
{
    [Fact]
    public async Task ConfirmationBindingNeverAuthorizesLegacyClaims()
    {
        var root = Path.Combine(Path.GetTempPath(), $"craft-architect-confirmed-claim-binding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var companyId = new CompanyId(Guid.NewGuid());
            var victimCrafter = Guid.NewGuid();
            var earlierCrafter = Guid.NewGuid();
            var attacker = Guid.NewGuid();
            var victim = Guid.NewGuid();
            var membershipPath = Path.Combine(root, "memberships.db");

            // A database from before confirmation bindings, holding one existing binding.
            await using (var legacy = new SqliteConnection($"Data Source={membershipPath};Pooling=False"))
            {
                await legacy.OpenAsync();
                await using var create = legacy.CreateCommand();
                create.CommandText = $"""
                    CREATE TABLE company_crafter_account_bindings (
                        company_id TEXT NOT NULL,
                        legacy_crafter_id TEXT NOT NULL,
                        account_profile_id TEXT NOT NULL,
                        evidence_kind TEXT NOT NULL CHECK(evidence_kind IN ('committed_discord_claim', 'operator_confirmed')),
                        actor_profile_id TEXT NULL,
                        created_at_utc TEXT NOT NULL,
                        updated_at_utc TEXT NOT NULL,
                        PRIMARY KEY(company_id, legacy_crafter_id)
                    );
                    INSERT INTO company_crafter_account_bindings VALUES (
                        '{companyId}', '{earlierCrafter:D}', '{victim:D}', 'committed_discord_claim',
                        NULL, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var memberships = new SqliteMembershipStore(
                new TradeMembershipOptions { DatabasePath = membershipPath },
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            var resolver = new LegacyCrafterAccountResolver(
                new SqliteProfileHostStore(new ProfileHostOptions { Enabled = true, DatabasePath = Path.Combine(root, "profiles.db") }),
                memberships,
                new SqliteDiscordIdentityStore(new DiscordIdentityOptions { DatabasePath = Path.Combine(root, "identity.db") }),
                new SqliteDiscordNotificationStore(new DiscordCommissionOptions { DatabasePath = Path.Combine(root, "discord.db") }),
                NullLogger<LegacyCrafterAccountResolver>.Instance);

            // The attacker claimed with the victim's public Lodestone ID and a commissioner
            // confirmed it: the reused crafter is bound for reuse only.
            await resolver.BindConfirmedClaimCrafterAsync(
                companyId,
                new CompanyCommissionClaim(
                    Guid.NewGuid(),
                    1,
                    DateTime.UtcNow,
                    victimCrafter,
                    victimCrafter,
                    new CompanyCommissionClaimAccountEvidence(attacker, "111111111111111111", "Attacker")));
            var binding = (await memberships.LoadCrafterBindingsAsync(companyId))
                .Single(item => item.LegacyCrafterId == victimCrafter);
            Assert.Equal((attacker, CrafterAccountBindingEvidence.ConfirmedClaimReuse), (binding.AccountProfileId, binding.Evidence));

            var legacyClaim = new CompanyCommissionClaim(Guid.NewGuid(), 1, DateTime.UtcNow.AddMonths(-3), victimCrafter, null);
            Assert.False(await resolver.IsClaimOwnedByAccountAsync(companyId, legacyClaim, attacker));

            // Strong evidence replaces the reuse binding and does authorize legacy claims.
            Assert.Equal(
                CrafterAccountBindingMutationStatus.Applied,
                (await memberships.BindCrafterAsync(
                    companyId,
                    victimCrafter,
                    victim,
                    CrafterAccountBindingEvidence.OperatorConfirmed,
                    Guid.NewGuid())).Status);
            Assert.True(await resolver.IsClaimOwnedByAccountAsync(companyId, legacyClaim, victim));
            Assert.False(await resolver.IsClaimOwnedByAccountAsync(companyId, legacyClaim, attacker));
            Assert.Contains(
                await memberships.LoadCrafterBindingsAsync(companyId),
                item => item.LegacyCrafterId == earlierCrafter && item.AccountProfileId == victim);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
