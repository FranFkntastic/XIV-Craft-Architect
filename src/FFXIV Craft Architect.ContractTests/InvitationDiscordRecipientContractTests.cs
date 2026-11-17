using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

/// <summary>
/// An invitation DM carries a live membership link. It must only ever reach the Discord
/// account the host verified for the invited crafter record; a wrong recipient hands the
/// record's history to a stranger and cannot be recalled.
/// </summary>
public sealed class InvitationDiscordRecipientContractTests
{
    private static readonly CompanyId CompanyId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly Guid BoundRecordId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid UnboundRecordId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ClaimantAccountId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid UnrelatedAccountId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private const string ClaimantDiscordId = "100000000000000002";
    private const string UnrelatedDiscordId = "100000000000000007";

    [Fact]
    public async Task InvitationDmGoesOnlyToTheDiscordAccountLinkedToTheBoundRecord()
    {
        var path = Path.Combine(Path.GetTempPath(), $"craft-architect-invitation-dm-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DiscordCommissionOptions
            {
                Enabled = true,
                CompanyId = CompanyId.Value.ToString("D"),
                ApplicationId = "100000000000000001",
                PublicKey = new string('a', 64),
                BotToken = "test-token",
                AllowedGuildId = "100000000000000003",
                AllowedChannelId = "100000000000000004",
                CommissionBaseUrl = "https://example.test/commission/",
                DatabasePath = path
            };
            var memberships = new SqliteMembershipStore(
                new TradeMembershipOptions { DatabasePath = path },
                TimeProvider.System,
                NullLogger<SqliteMembershipStore>.Instance);
            var identities = new SqliteDiscordIdentityStore(new DiscordIdentityOptions { DatabasePath = path });
            Assert.Equal(
                DiscordIdentityLinkResultStatus.Linked,
                (await identities.LinkAsync(ClaimantAccountId, ClaimantDiscordId, "Claimant", DateTimeOffset.UtcNow)).Status);
            Assert.Equal(
                DiscordIdentityLinkResultStatus.Linked,
                (await identities.LinkAsync(UnrelatedAccountId, UnrelatedDiscordId, "Unrelated", DateTimeOffset.UtcNow)).Status);
            await memberships.BindCrafterAsync(
                CompanyId,
                BoundRecordId,
                ClaimantAccountId,
                CrafterAccountBindingEvidence.ConfirmedClaimReuse,
                actorProfileId: null);
            var discord = new RecordingDmClient();
            var delivery = new MembershipInvitationDiscordDelivery(
                memberships,
                identities,
                new SqliteDiscordCollaborationStore(options),
                discord,
                options,
                NullLogger<MembershipInvitationDiscordDelivery>.Instance);

            var bound = await memberships.IssueInvitationAsync(CompanyId, UnrelatedAccountId, BoundRecordId);
            var sent = await delivery.DeliverAsync(bound, "Company", "Crafter");
            Assert.Equal(MembershipInvitationDeliveryCodes.Sent, sent.Code);
            Assert.Equal([ClaimantDiscordId], discord.Recipients);

            // A second delivery of the same invitation never sends again.
            Assert.Equal(MembershipInvitationDeliveryCodes.Sent, (await delivery.DeliverAsync(bound, "Company", "Crafter")).Code);
            Assert.Equal([ClaimantDiscordId], discord.Recipients);

            // No binding means no verified person: nothing is sent, not even to the issuer.
            var unbound = await memberships.IssueInvitationAsync(CompanyId, UnrelatedAccountId, UnboundRecordId);
            Assert.Equal(
                MembershipInvitationDeliveryCodes.NoRecipient,
                (await delivery.DeliverAsync(unbound, "Company", "Other")).Code);
            var open = await memberships.IssueInvitationAsync(CompanyId, UnrelatedAccountId, null);
            Assert.Equal(
                MembershipInvitationDeliveryCodes.NoRecipient,
                (await delivery.DeliverAsync(open, "Company", null)).Code);
            Assert.Equal([ClaimantDiscordId], discord.Recipients);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private sealed class RecordingDmClient : IDiscordApiClient
    {
        public List<string> Recipients { get; } = [];

        public Task<DiscordApiResult> ResolveDirectMessageChannelAsync(
            string recipientUserId,
            CancellationToken cancellationToken = default)
        {
            Recipients.Add(recipientUserId);
            return Task.FromResult(new DiscordApiResult(DiscordApiOutcome.Succeeded, "100000000000000099"));
        }

        public Task<DiscordApiResult> CreateNotificationMessageAsync(
            string channelId,
            object payload,
            string? allowedMentionUserId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscordApiResult(DiscordApiOutcome.Succeeded, "100000000000000100"));

        public Task<DiscordApiResult> CreateMessageAsync(string channelId, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> EditMessageAsync(string channelId, string messageId, object payload, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiscordApiResult> GetMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
