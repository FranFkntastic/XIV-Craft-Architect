using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Identity;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordIdentityContractTests
{
    private const string DiscordUser = "111111111111111111";
    private const string OtherDiscordUser = "222222222222222222";
    private const string ParticipantCredential =
        "participant_credential_contract_aaaaaaaaaaaaaaaa";
    private const string OtherParticipantCredential =
        "participant_credential_contract_bbbbbbbbbbbbbbbb";
    [Fact]
    public async Task OAuthLinksAndDiscordActionsRecheckCanonicalTradeAuthority()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifyDiscordIdentityContractAsync(root);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiscordComponentsBindClaimsToCommittedContactsAndCanonicalAuthority()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"craft-architect-discord-components-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = new MutableTimeProvider(
                DateTimeOffset.Parse("2026-08-06T12:00:00Z"));
            var companyId = new CompanyId(Guid.NewGuid());
            var commissionId = Guid.NewGuid();
            const string publicId = "discord-component-public-id";
            const string interactionId = "333333333333333333";
            const string ownerInteractionId = "444444444444444444";
            var actionToken = SqliteDiscordCollaborationStore.CreateActionToken();
            var discordOptions = new DiscordCommissionOptions
            {
                Enabled = true,
                CompanyId = companyId.Value.ToString("D"),
                ApplicationId = "100000000000000001",
                PublicKey = new string('a', 64),
                BotToken = "test-token",
                AllowedGuildId = "100000000000000002",
                AllowedChannelId = "100000000000000003",
                CommissionBaseUrl = "https://example.test/commission.html?id=",
                DatabasePath = Path.Combine(root, "discord.db")
            };
            var collaboration = new SqliteDiscordCollaborationStore(discordOptions);
            var created = await collaboration.CreatePublicationAsync(
                new TradeCompanyPublicationOwnership(
                    companyId,
                    commissionId,
                    new CompanyRecordRevision(1)),
                publicId,
                1,
                "component-contract",
                actionToken,
                discordOptions.AllowedChannelId,
                DiscordPublicationState.Open,
                "{\"content\":\"public payload has no interaction claim URL\"}",
                now.GetUtcNow());
            Assert.True(created.Success, created.Error);

            var identityOptions = CreateOptions(root);
            var identities = new SqliteDiscordIdentityStore(identityOptions);
            var profiles = new SqliteProfileHostStore(new ProfileHostOptions
            {
                Enabled = true,
                DatabasePath = Path.Combine(root, "profiles.db")
            });
            var hasher = new ProfileAccessKeyHasher();
            var participantProfileId = Guid.NewGuid();
            await profiles.EnsureProfileAsync(
                participantProfileId.ToString("D"),
                "Participant",
                "cap_discord-participant-contract-key",
                hasher,
                CancellationToken.None);
            var notifications = new SqliteDiscordNotificationStore(discordOptions);
            var claimCapabilityId = Guid.NewGuid();
            var claimIssuer = new StubClaimLinkIssuer
            {
                Link = new DiscordInteractionClaimLink(
                    new Uri(
                        "https://example.test/commission.html?id=discord-component-public-id#claim=ephemeral_claim_token"),
                    claimCapabilityId,
                    5)
            };
            var ownerUri = new Uri(
                $"https://app.test/trade/orders?orderId={commissionId:D}");
            var resolver = new StubInteractionAccessResolver
            {
                Resolution = new DiscordInteractionAccessResolution(
                    DiscordInteractionAccessStatus.Authorized,
                    participantProfileId,
                    IsCompanyOperator: true,
                    IsActiveParticipant: false,
                    [new DiscordInteractionAction(
                        DiscordInteractionActionKind.OpenOwnerOrder,
                        "Open in Trade Architect",
                        ownerUri,
                        DiscordInteractionActionDelivery.EphemeralOnly)])
            };
            var interactions = new DiscordCommissionInteractionService(
                discordOptions,
                collaboration,
                identities,
                profiles,
                notifications,
                claimIssuer,
                resolver,
                now);

            var unlinked = await interactions.HandleAsync(
                ComponentInteraction(
                    interactionId,
                    DiscordUser,
                    $"claim-discord:{actionToken}"));
            using (var response = JsonDocument.Parse(JsonSerializer.Serialize(unlinked)))
            {
                Assert.Equal(64, response.RootElement.GetProperty("flags").GetInt32());
                Assert.Contains(
                    "Link Discord in Craft Architect Options",
                    response.RootElement.GetProperty("content").GetString());
            }
            Assert.Null(await notifications.LoadPendingClaimContactAsync(
                companyId,
                commissionId,
                publicId,
                claimCapabilityId,
                5,
                now.GetUtcNow()));

            var inactiveProfileId = Guid.NewGuid();
            await profiles.EnsureProfileAsync(
                inactiveProfileId.ToString("D"),
                "Inactive participant",
                "cap_discord-inactive-contract-key",
                hasher,
                CancellationToken.None);
            await identities.LinkAsync(
                inactiveProfileId,
                OtherDiscordUser,
                "Inactive Discord Participant",
                now.GetUtcNow());
            await profiles.DisableProfileAsync(
                inactiveProfileId.ToString("D"),
                CancellationToken.None);
            var inactive = await interactions.HandleAsync(
                ComponentInteraction(
                    "888888888888888888",
                    OtherDiscordUser,
                    $"claim-discord:{actionToken}"));
            using (var response = JsonDocument.Parse(JsonSerializer.Serialize(inactive)))
            {
                Assert.Contains(
                    "Link Discord in Craft Architect Options",
                    response.RootElement.GetProperty("content").GetString());
            }
            Assert.Null(await notifications.LoadPendingClaimContactAsync(
                companyId,
                commissionId,
                publicId,
                claimCapabilityId,
                5,
                now.GetUtcNow()));

            Assert.Equal(
                DiscordIdentityLinkResultStatus.Linked,
                (await identities.LinkAsync(
                    participantProfileId,
                    DiscordUser,
                    "Discord Participant",
                    now.GetUtcNow())).Status);
            var linked = await interactions.HandleAsync(
                ComponentInteraction(
                    interactionId,
                    DiscordUser,
                    $"claim-discord:{actionToken}"));
            using (var response = JsonDocument.Parse(JsonSerializer.Serialize(linked)))
            {
                Assert.Equal(64, response.RootElement.GetProperty("flags").GetInt32());
                var claimUrl = response.RootElement
                    .GetProperty("components")[0]
                    .GetProperty("components")[0]
                    .GetProperty("url")
                    .GetString();
                Assert.Contains("#claim=ephemeral_claim_token", claimUrl);
            }
            var expectation = await notifications.LoadPendingClaimContactAsync(
                companyId,
                commissionId,
                publicId,
                claimCapabilityId,
                5,
                now.GetUtcNow());
            Assert.NotNull(expectation);
            Assert.Equal(DiscordUser, expectation.Contact.DiscordUserId);

            var ownerResponse = await interactions.HandleAsync(
                ComponentInteraction(
                    ownerInteractionId,
                    DiscordUser,
                    $"open-workspace:{actionToken}"));
            using (var response = JsonDocument.Parse(JsonSerializer.Serialize(ownerResponse)))
            {
                Assert.Equal(64, response.RootElement.GetProperty("flags").GetInt32());
                var links = response.RootElement
                    .GetProperty("components")[0]
                    .GetProperty("components");
                Assert.Equal(1, links.GetArrayLength());
                Assert.Equal(ownerUri.AbsoluteUri, links[0].GetProperty("url").GetString());
            }
            Assert.Equal(0, resolver.ParticipantEntryCalls);

            var delivery = new CompanyCommissionDiscordDeliveryService(
                collaboration,
                notifications,
                discordOptions,
                now);
            var committer = new DiscordClaimContactCommitter(
                notifications,
                delivery,
                now);
            var capability = new CompanyCommissionCapabilityResolution(
                companyId,
                commissionId,
                publicId,
                CompanyCommissionCapabilityKind.Claim,
                null,
                5,
                claimCapabilityId);
            var claimId = Guid.NewGuid();
            var mutation = ClaimMutation(
                commissionId,
                now.GetUtcNow().UtcDateTime,
                CreateAssignedCommission(
                    companyId,
                    commissionId,
                    publicId,
                    new TradeCompanyPublicationOwnership(
                        companyId,
                        commissionId,
                        new CompanyRecordRevision(1)),
                    now.GetUtcNow().UtcDateTime,
                    claimId));
            Assert.True(await committer.CaptureAsync(capability, mutation));
            Assert.True(await notifications.HasCommittedClaimContactAsync(
                companyId,
                commissionId,
                claimId,
                DiscordUser));
            Assert.False(await notifications.HasCommittedClaimContactAsync(
                companyId,
                commissionId,
                Guid.NewGuid(),
                DiscordUser));
            Assert.Null(await notifications.LoadPendingClaimContactAsync(
                companyId,
                commissionId,
                publicId,
                claimCapabilityId,
                5,
                now.GetUtcNow()));
            Assert.False(await committer.CaptureAsync(capability, mutation));
            Assert.False(await committer.CaptureAsync(
                capability with { CapabilityId = Guid.NewGuid() },
                mutation));

            var expiredCapabilityId = Guid.NewGuid();
            Assert.True(await notifications.RecordPendingClaimContactAsync(
                new PendingDiscordClaimContactExpectation(
                    companyId,
                    commissionId,
                    publicId,
                    expiredCapabilityId,
                    5,
                    "555555555555555555",
                    new DiscordOriginContact(OtherDiscordUser, "Expired claimant"),
                    now.GetUtcNow(),
                    now.GetUtcNow() + TimeSpan.FromMinutes(1))));
            now.Advance(TimeSpan.FromMinutes(2));
            Assert.False(await committer.CaptureAsync(
                capability with { CapabilityId = expiredCapabilityId },
                mutation));
            Assert.False(await notifications.HasCommittedClaimContactAsync(
                companyId,
                commissionId,
                claimId,
                OtherDiscordUser));

            var ownerProfileId = Guid.NewGuid();
            await profiles.EnsureProfileAsync(
                ownerProfileId.ToString("D"),
                "Company owner",
                "cap_discord-company-owner-contract-key",
                hasher,
                CancellationToken.None);
            var companies = new ProfileHostedTradeCompanyService(profiles, hasher);
            var ownerAccess = new TradeCompanyAccessContext(
                companyId,
                ownerProfileId,
                TradeCompanyRole.Owner,
                ownerProfileId);
            var ownership = new TradeCompanyPublicationOwnership(
                companyId,
                commissionId,
                new CompanyRecordRevision(1));
            var companyProfile = new TradeCompanyProfile
            {
                Id = companyId.Value,
                Name = "Discord Contract Company",
                CreatedAtUtc = now.GetUtcNow().UtcDateTime,
                UpdatedAtUtc = now.GetUtcNow().UtcDateTime
            };
            Assert.True((await profiles.PutObjectAsync(
                ownerProfileId.ToString("D"),
                ProfileSyncCollections.TradeCompanyProfiles,
                companyId.ToString(),
                JsonSerializer.Serialize(companyProfile),
                0,
                CancellationToken.None)).Success);
            var canonicalOrder = CreateAssignedCommission(
                companyId,
                commissionId,
                publicId,
                ownership,
                now.GetUtcNow().UtcDateTime,
                claimId);
            Assert.True((await companies.PutRecordAsync(
                ownerAccess,
                TradeCompanyRecordKinds.Order,
                commissionId.ToString("D"),
                JsonSerializer.Serialize(canonicalOrder),
                CompanyRecordRevision.None,
                "discord-authority-order")).Success);
            Assert.True((await companies.PutRecordAsync(
                ownerAccess,
                TradeCompanyRecordKinds.Publication,
                publicId,
                JsonSerializer.Serialize(ownership),
                CompanyRecordRevision.None,
                "discord-authority-publication")).Success);
            var commissions = new HostedCompanyCommissionService(
                companies,
                profiles,
                now,
                [],
                NullLogger<HostedCompanyCommissionService>.Instance);
            var authority = new HostedDiscordInteractionAuthority(
                profiles,
                companies,
                commissions,
                notifications);
            var linkedIdentity = await identities.LoadByDiscordUserAsync(DiscordUser);
            var authorized = await authority.ResolveAsync(
                linkedIdentity!,
                new DiscordInteractionTarget(
                    "666666666666666666",
                    DiscordUser,
                    companyId,
                    commissionId,
                    publicId));
            Assert.NotNull(authorized);
            Assert.True(authorized.IsActiveParticipant);
            Assert.False(authorized.IsCompanyOperator);
            var canonicalResolver = new DiscordInteractionAccessResolver(
                identityOptions,
                identities,
                authority,
                new SqliteCompanyCommissionCapabilityStore(
                    new CommissionBriefOptions
                    {
                        DatabasePath = Path.Combine(root, "participant-capabilities.db")
                    }),
                now);
            var participantInteractions = new DiscordCommissionInteractionService(
                discordOptions,
                collaboration,
                identities,
                profiles,
                notifications,
                claimIssuer,
                canonicalResolver,
                now);
            var participantResponse = await participantInteractions.HandleAsync(
                ComponentInteraction(
                    "777777777777777777",
                    DiscordUser,
                    $"open-workspace:{actionToken}"));
            using var participantPayload = JsonDocument.Parse(
                JsonSerializer.Serialize(participantResponse));
            Assert.Equal(
                64,
                participantPayload.RootElement.GetProperty("flags").GetInt32());
            Assert.StartsWith(
                "#bootstrap=",
                new Uri(participantPayload.RootElement
                    .GetProperty("components")[0]
                    .GetProperty("components")[0]
                    .GetProperty("url")
                    .GetString()!).Fragment,
                StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
    private static async Task VerifyDiscordIdentityContractAsync(string root)
    {
        var now = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        var options = CreateOptions(root);
        options.Validate();
        var linkStore = new SqliteDiscordIdentityStore(options);
        var profiles = new SqliteProfileHostStore(new ProfileHostOptions
        {
            Enabled = true,
            DatabasePath = Path.Combine(root, "profiles.db")
        });
        var hasher = new ProfileAccessKeyHasher();
        var ownerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var owner = (await profiles.EnsureProfileAsync(
            ownerId.ToString("D"),
            "Owner",
            "cap_discord-owner-contract-key",
            hasher,
            CancellationToken.None)).Profile;
        var other = (await profiles.EnsureProfileAsync(
            otherId.ToString("D"),
            "Other",
            "cap_discord-other-contract-key",
            hasher,
            CancellationToken.None)).Profile;
        var oauth = new StubDiscordOAuthClient();
        var linking = new DiscordIdentityLinkService(
            options,
            linkStore,
            profiles,
            oauth,
            now);
        var start = await linking.StartAsync(owner);
        var authorize = new Uri(start.AuthorizationUrl);
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(options.AuthorizationEndpoint, authorize.GetLeftPart(UriPartial.Path));
        Assert.Equal(options.ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(options.CallbackUri, query["redirect_uri"]);
        Assert.Equal("identify", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));

        oauth.Identity = new DiscordOAuthIdentity(DiscordUser, "owner-context-only");
        var linked = await linking.CompleteAsync("oauth-code", query["state"]);
        Assert.Equal(DiscordLinkCompletionStatus.Linked, linked.Status);
        Assert.Equal(
            query["code_challenge"],
            Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(oauth.LastVerifier!))));
        Assert.Equal(
            DiscordLinkCompletionStatus.ReplayedState,
            (await linking.CompleteAsync("oauth-code", query["state"])).Status);
        var conflictingDiscord = await linking.StartAsync(other);
        oauth.Identity = new DiscordOAuthIdentity(DiscordUser, "conflicting-display");
        Assert.Equal(
            DiscordLinkCompletionStatus.Conflict,
            (await linking.CompleteAsync(
                "oauth-code",
                StateFrom(conflictingDiscord))).Status);
        var conflictingProfile = await linking.StartAsync(owner);
        oauth.Identity = new DiscordOAuthIdentity(OtherDiscordUser, "other-display");
        Assert.Equal(
            DiscordLinkCompletionStatus.Conflict,
            (await linking.CompleteAsync(
                "oauth-code",
                StateFrom(conflictingProfile))).Status);
        Assert.Equal(
            DiscordUser,
            (await linkStore.LoadByProfileAsync(ownerId))!.DiscordUserId);

        var stale = await linking.StartAsync(other);
        now.Advance(options.StateLifetime + TimeSpan.FromSeconds(1));
        Assert.Equal(
            DiscordLinkCompletionStatus.ExpiredState,
            (await linking.CompleteAsync("oauth-code", StateFrom(stale))).Status);
        var auditKinds = (await linkStore.LoadAuditAsync(ownerId))
            .Select(item => item.EventKind)
            .ToArray();
        Assert.Contains("linked", auditKinds);
        Assert.Contains("profile_link_conflict", auditKinds);
        Assert.Contains("oauth_consumed", auditKinds);
        var companyId = new CompanyId(Guid.NewGuid());
        var commissionId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        const string publicId = "discord-contract-public-id";
        var authority = new StubCanonicalAuthority
        {
            ProfileId = ownerId,
            CompanyId = companyId,
            CommissionId = commissionId,
            PublicBriefId = publicId,
            GrantId = grantId,
            CapabilityRevision = 7,
            IsCompanyOperator = true,
            IsActiveParticipant = true
        };
        var capabilities = new SqliteCompanyCommissionCapabilityStore(
            new CommissionBriefOptions
            {
                DatabasePath = Path.Combine(root, "capabilities.db")
            });
        var resolver = new DiscordInteractionAccessResolver(
            options,
            linkStore,
            authority,
            capabilities,
            now);
        var target = new DiscordInteractionTarget(
            "333333333333333333",
            DiscordUser,
            companyId,
            commissionId,
            publicId);

        var allowed = await resolver.ResolveAsync(target);
        Assert.True(allowed.Authorized);
        Assert.True(allowed.IsCompanyOperator);
        var ownerAction = Assert.Single(allowed.Actions);
        Assert.Equal(DiscordInteractionActionKind.OpenOwnerOrder, ownerAction.Kind);
        Assert.Equal(DiscordInteractionActionDelivery.EphemeralOnly, ownerAction.Delivery);
        Assert.Equal("https", ownerAction.Uri.Scheme);
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target with
            {
                DiscordUserId = OtherDiscordUser
            })).Status);
        authority.Available = false;
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target)).Status);
        authority.Available = true;

        var staleEntry = await resolver.IssueParticipantEntryAsync(target);
        var staleToken = BootstrapFrom(staleEntry);
        Assert.DoesNotContain(ParticipantCredential, staleEntry.Actions[1].Uri.AbsoluteUri);
        authority.IsActiveParticipant = false;
        Assert.Null(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                staleToken,
                ParticipantCredential)));

        authority.IsActiveParticipant = true;
        authority.CapabilityRevision++;
        var currentTarget = target with { InteractionId = "444444444444444444" };
        var currentEntry = await resolver.IssueParticipantEntryAsync(currentTarget);
        var currentAction = Assert.Single(
            currentEntry.Actions,
            item => item.Kind == DiscordInteractionActionKind.OpenParticipantCommission);
        Assert.Equal(DiscordInteractionActionDelivery.EphemeralOnly, currentAction.Delivery);
        Assert.StartsWith("bootstrap=", currentAction.Uri.Fragment.TrimStart('#'));
        var currentToken = BootstrapFrom(currentEntry);
        Assert.Equal(
            publicId,
            (await resolver.ExchangeAsync(
                new DiscordParticipantExchangeRequest(
                    currentToken,
                    ParticipantCredential)))!.PublicBriefId);
        Assert.NotNull(await capabilities.ResolveAsync(
            publicId,
            CompanyCommissionCapabilityKind.Participant,
            ParticipantCredential));
        Assert.NotNull(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                currentToken,
                ParticipantCredential)));
        Assert.Null(await resolver.ExchangeAsync(
            new DiscordParticipantExchangeRequest(
                currentToken,
                OtherParticipantCredential)));
        Assert.Null(await capabilities.ResolveAsync(
            publicId,
            CompanyCommissionCapabilityKind.Participant,
            OtherParticipantCredential));

        Assert.True(await linking.UnlinkAsync(owner));
        Assert.Equal(
            DiscordInteractionAccessStatus.Forbidden,
            (await resolver.ResolveAsync(target)).Status);
        Assert.Contains(
            await linkStore.LoadAuditAsync(ownerId),
            item => item.EventKind == "unlinked");
    }

    private static JsonElement ComponentInteraction(
        string interactionId,
        string discordUserId,
        string customId) =>
        JsonSerializer.SerializeToElement(new
        {
            id = interactionId,
            guild_id = "100000000000000002",
            channel_id = "100000000000000003",
            member = new
            {
                nick = "Discord Participant",
                user = new
                {
                    id = discordUserId,
                    username = "participant"
                }
            },
            data = new
            {
                custom_id = customId
            }
        });

    private static CompanyCommissionMutationResult ClaimMutation(
        Guid commissionId,
        DateTime committedAtUtc,
        TradeOrder order) =>
        new(
            CompanyCommissionMutationStatus.Applied,
            Order: order,
            Activity: new CompanyCommissionActivityEvent
            {
                EventId = Guid.NewGuid(),
                CommandId = Guid.NewGuid(),
                CommissionId = commissionId,
                CommissionRevision = 2,
                Actor = new CompanyCommissionActor(
                    "claim-revision:5",
                    CompanyCommissionActorKind.Crafter),
                SourceSurface = CompanyCommissionSourceSurface.PublicBrief,
                CreatedAtUtc = committedAtUtc,
                Kind = CompanyCommissionActivityKind.ClaimAccepted,
                TermsVersion = 1
            });

    private static TradeOrder CreateAssignedCommission(
        CompanyId companyId,
        Guid commissionId,
        string publicId,
        TradeCompanyPublicationOwnership ownership,
        DateTime createdAtUtc,
        Guid? claimIdOverride = null)
    {
        var actor = new CompanyCommissionActor(
            "commissioner",
            CompanyCommissionActorKind.Commissioner);
        var claimId = claimIdOverride ?? Guid.NewGuid();
        var crafterId = Guid.NewGuid();
        var outputLineId = Guid.NewGuid();
        return new TradeOrder
        {
            Id = commissionId,
            CompanyProfileId = companyId.Value,
            Title = "Discord authority contract",
            Status = TradeOrderStatus.Assigned,
            AssignedCrafterId = crafterId,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            CommissionedAtUtc = createdAtUtc,
            CommissionPublication = new TradeCommissionPublication
            {
                PublicId = publicId,
                PublicUrl = $"https://example.test/commission.html?id={publicId}",
                Version = 1,
                PublishedAtUtc = createdAtUtc,
                Ownership = ownership
            },
            CompanyCommission = new TradeCompanyCommission
            {
                CommissionId = commissionId,
                CompanyId = companyId,
                CommissionerActorId = actor.ActorId,
                Reference = "CA-DISCORD-AUTHORITY",
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = createdAtUtc,
                CurrentTermsVersion = 1,
                TermsVersions =
                [
                    new CompanyCommissionTermsVersion
                    {
                        Version = 1,
                        CreatedAtUtc = createdAtUtc,
                        CreatedBy = actor,
                        Outputs =
                        [
                            new CompanyCommissionOutputTerm(
                                outputLineId,
                                100,
                                "Cobalt Joint Plate",
                                1,
                                false)
                        ],
                        Payment = new CompanyCommissionPaymentTerms(
                            CompanyCommissionPaymentSchedule.Advance,
                            "Contract payment",
                            0,
                            0,
                            1_000,
                            1_000),
                        PricingEvidence = new CompanyCommissionPricingEvidence(
                            "Selected routes",
                            "Aether",
                            "Siren",
                            createdAtUtc)
                    }
                ],
                PublicMetadata = new CompanyCommissionPublicMetadata
                {
                    PublicBriefId = publicId,
                    PublicUrl = $"https://example.test/commission.html?id={publicId}",
                    ViewState = CompanyCommissionPublicViewState.Published,
                    PublishedAtUtc = createdAtUtc,
                    LegacyOwnership = ownership
                },
                ActiveClaimCapabilityRevision = 5,
                ActiveClaim = new CompanyCommissionClaim(
                    claimId,
                    5,
                    createdAtUtc,
                    crafterId,
                    null),
                ParticipantGrant = new CompanyCommissionParticipantGrant(
                    Guid.NewGuid(),
                    claimId,
                    1,
                    1,
                    createdAtUtc),
                ParticipantAcknowledgedTermsVersion = 1,
                Gates = new CompanyCommissionGateState(
                    new CompanyCommissionIdentityClearance(
                        CompanyCommissionClearanceState.Satisfied),
                    new CompanyCommissionPaymentClearance(
                        CompanyCommissionClearanceState.Pending,
                        TermsVersion: 1),
                    new CompanyCommissionMaterialClearance(
                        CompanyCommissionClearanceState.NotRequired,
                        [])),
                OutputProgress = [],
                DeliveryReadiness = new CompanyCommissionDeliveryReadiness(false),
                SettlementState = CompanyCommissionSettlementState.NotDue
            }
        };
    }

    private static DiscordIdentityOptions CreateOptions(string root) => new()
    {
        Enabled = true,
        ClientId = "123456789012345678",
        ClientSecret = "client_secret_contract_aaaaaaaaaaaaaaaa",
        BootstrapSecret = "bootstrap_secret_contract_bbbbbbbbbbbbbbbb",
        CallbackUri = "https://identity.test/api/identity/v1/discord/callback",
        ApplicationBaseUri = "https://app.test/",
        DatabasePath = Path.Combine(root, "discord-identity.db")
    };

    private static string StateFrom(DiscordLinkStartResponse response) =>
        QueryHelpers.ParseQuery(new Uri(response.AuthorizationUrl).Query)["state"]!;

    private static string BootstrapFrom(DiscordInteractionAccessResolution resolution) =>
        Assert.Single(
            resolution.Actions,
            item => item.Kind == DiscordInteractionActionKind.OpenParticipantCommission)
        .Uri.Fragment["#bootstrap=".Length..];

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class StubDiscordOAuthClient : IDiscordOAuthClient
    {
        public DiscordOAuthIdentity? Identity { get; set; }
        public string? LastVerifier { get; private set; }

        public Task<DiscordOAuthIdentity?> ResolveIdentityAsync(
            string code,
            string pkceVerifier,
            string callbackUri,
            CancellationToken cancellationToken = default)
        {
            LastVerifier = pkceVerifier;
            return Task.FromResult(Identity);
        }
    }

    private sealed class StubCanonicalAuthority : IDiscordCanonicalInteractionAuthority
    {
        public bool Available { get; set; } = true;
        public Guid ProfileId { get; init; }
        public CompanyId CompanyId { get; init; }
        public Guid CommissionId { get; init; }
        public string PublicBriefId { get; init; } = string.Empty;
        public Guid GrantId { get; init; }
        public long CapabilityRevision { get; set; }
        public bool IsCompanyOperator { get; init; }
        public bool IsActiveParticipant { get; set; }

        public Task<DiscordParticipantAuthority?> ResolveAsync(
            DiscordIdentityLink link,
            DiscordInteractionTarget target,
            CancellationToken cancellationToken = default)
        {
            DiscordParticipantAuthority? result = Available &&
                link.ProfileId == ProfileId &&
                link.DiscordUserId == target.DiscordUserId &&
                target.CompanyId == CompanyId &&
                target.CommissionId == CommissionId &&
                target.PublicBriefId == PublicBriefId
                    ? new DiscordParticipantAuthority(
                        ProfileId,
                        link.DiscordUserId,
                        CompanyId,
                        CommissionId,
                        PublicBriefId,
                        GrantId,
                        CapabilityRevision,
                        new Uri($"https://brief.test/commission.html?id={PublicBriefId}"),
                        IsCompanyOperator,
                        IsActiveParticipant)
                    : null;
            return Task.FromResult(result);
        }
    }

    private sealed class StubClaimLinkIssuer : IDiscordInteractionClaimLinkIssuer
    {
        public DiscordInteractionClaimLink? Link { get; init; }

        public Task<DiscordInteractionClaimLink?> IssueInteractionClaimLinkAsync(
            DiscordPublicationRecord publication,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Link);
    }

    private sealed class StubInteractionAccessResolver : IDiscordInteractionAccessResolver
    {
        public required DiscordInteractionAccessResolution Resolution { get; init; }
        public int ParticipantEntryCalls { get; private set; }

        public Task<DiscordInteractionAccessResolution> ResolveAsync(
            DiscordInteractionTarget target,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Resolution);

        public Task<DiscordInteractionAccessResolution> IssueParticipantEntryAsync(
            DiscordInteractionTarget target,
            CancellationToken cancellationToken = default)
        {
            ParticipantEntryCalls++;
            return Task.FromResult(Resolution);
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
