using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using FFXIV_Craft_Architect.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WebMarketAnalysisEngineSettlementTests
{
    [Fact]
    public async Task Settlement_EnforcesOrderAndReturnsExplicitCommitOutcome()
    {
        var fixture = new SettlementFixture(namedPlan: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Persisting));

        var publishing = await fixture.SettleAsync(EnginePhase.Publishing);
        var persisting = await fixture.SettleAsync(EnginePhase.Persisting);
        var ui = await fixture.SettleAsync(EnginePhase.SettlingUi);
        var postAction = await fixture.SettleAsync(EnginePhase.CapturingPostActionEvidence);
        var gate = await fixture.SettleAsync(EnginePhase.ReleasingGate);

        Assert.Equal(EngineSettlementOutcome.Applied, publishing.Outcome);
        Assert.Equal(EngineSettlementOutcome.Committed, persisting.Outcome);
        Assert.Equal(EngineSettlementOutcome.Applied, ui.Outcome);
        Assert.Equal(EngineSettlementOutcome.Applied, postAction.Outcome);
        Assert.Equal(EngineSettlementOutcome.Applied, gate.Outcome);
        Assert.Equal(["named", "autosave"], fixture.Store.Calls);
        Assert.Single(fixture.AppState.MarketItemAnalyses);
        Assert.False(fixture.Settlement.Capability.IsReady);
        Assert.False(fixture.Settlement.Capability.HasDurableLedger);
    }

    [Fact]
    public async Task Settlement_PublicAdapterCannotBypassUnavailableCapability()
    {
        var fixture = new SettlementFixture(enableTestExecution: false);
        var registration = new EngineExecutionContextRegistration(
            fixture.Generation,
            Guid.NewGuid(),
            fixture.Request,
            EngineCanonicalHash.Compute(fixture.Request, EngineJsonSerializerOptions.CreateWire()),
            "invocation",
            "claim");

        var register = Assert.Throws<NotSupportedException>(() =>
            fixture.Settlement.RegisterExecutionContext(registration));
        var settle = await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Settlement.SettleAsync(
                EnginePhase.Publishing,
                fixture.CreateContext(EnginePhase.Publishing),
                CancellationToken.None));
        var observe = await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Settlement.ObserveAsync(
                EnginePhase.Publishing,
                fixture.CreateContext(EnginePhase.Publishing),
                CancellationToken.None));

        Assert.Equal(fixture.Settlement.Capability.UnsupportedReason, register.Message);
        Assert.Equal(register.Message, settle.Message);
        Assert.Equal(register.Message, observe.Message);
        Assert.Empty(fixture.AppState.MarketItemAnalyses);
    }

    [Theory]
    [InlineData(EnginePhase.Publishing)]
    [InlineData(EnginePhase.Persisting)]
    [InlineData(EnginePhase.SettlingUi)]
    [InlineData(EnginePhase.CapturingPostActionEvidence)]
    [InlineData(EnginePhase.ReleasingGate)]
    public async Task Settlement_RejectsStaleGenerationAtEveryPhase(EnginePhase stalePhase)
    {
        var fixture = new SettlementFixture();
        var phases = new[]
        {
            EnginePhase.Publishing,
            EnginePhase.Persisting,
            EnginePhase.SettlingUi,
            EnginePhase.CapturingPostActionEvidence,
            EnginePhase.ReleasingGate
        };
        foreach (var phase in phases.TakeWhile(phase => phase != stalePhase))
        {
            await fixture.SettleAsync(phase);
        }
        fixture.Generation++;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(stalePhase));

        if (stalePhase == EnginePhase.Publishing)
        {
            Assert.Empty(fixture.AppState.MarketItemAnalyses);
            Assert.Empty(fixture.Store.Calls);
        }
    }

    [Fact]
    public async Task Settlement_DuplicateDeliveryReturnsRecordedEvidence()
    {
        var fixture = new SettlementFixture();
        var changed = 0;
        fixture.AppState.OnShoppingListChanged += () => changed++;

        var first = await fixture.SettleAsync(EnginePhase.Publishing);
        var version = fixture.AppState.CurrentVersions.MarketAnalysisVersion;
        var duplicate = await fixture.SettleAsync(EnginePhase.Publishing);

        Assert.Equal(first, duplicate);
        Assert.Equal(version, fixture.AppState.CurrentVersions.MarketAnalysisVersion);
        Assert.Equal(1, changed);
        Assert.Single(fixture.AppState.MarketItemAnalyses);
    }

    [Fact]
    public async Task Settlement_DuplicateGateDeliveryDoesNotReleaseAgain()
    {
        var fixture = new SettlementFixture();

        var first = await fixture.SettleAsync(EnginePhase.ReleasingGate);
        var duplicate = await fixture.SettleAsync(EnginePhase.ReleasingGate);

        Assert.Equal(first, duplicate);
        Assert.Equal(EngineSettlementOutcome.Applied, duplicate.Outcome);
        Assert.Equal(1, fixture.Gate.ReleaseAttempts);
    }

    [Fact]
    public async Task Settlement_CancellationBeforePublicationHasNoEffects()
    {
        var fixture = new SettlementFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.SettleAsync(EnginePhase.Publishing, cancellation.Token));
        var observed = await fixture.ObserveAsync(EnginePhase.Publishing);

        Assert.Equal(EngineSettlementOutcome.NotApplied, observed.Outcome);
        Assert.Empty(fixture.AppState.MarketItemAnalyses);
        Assert.Empty(fixture.Store.Calls);
    }

    [Fact]
    public async Task Settlement_PartialDurablePersistenceIsIndeterminateUntilRetryCompletes()
    {
        var fixture = new SettlementFixture(namedPlan: true);
        fixture.Store.ThrowOnAutoSave = true;
        await fixture.SettleAsync(EnginePhase.Publishing);

        await Assert.ThrowsAsync<IOException>(() => fixture.SettleAsync(EnginePhase.Persisting));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ObserveAsync(EnginePhase.Persisting));

        fixture.Store.ThrowOnAutoSave = false;
        var reconciled = await fixture.SettleAsync(EnginePhase.Persisting);

        Assert.Equal(EngineSettlementOutcome.Committed, reconciled.Outcome);
        Assert.Equal(1, fixture.Store.NamedSaveCount);
        Assert.Equal(2, fixture.Store.AutoSaveCount);
    }

    [Fact]
    public async Task Settlement_RejectsAppStateSemanticHashMismatch()
    {
        var fixture = new SettlementFixture();
        await fixture.SettleAsync(EnginePhase.Publishing);
        await fixture.SettleAsync(EnginePhase.Persisting);
        fixture.AppState.ReplaceMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 2, Name = "Other", QuantityNeeded = 1 }],
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.SettlingUi));
    }

    [Theory]
    [InlineData("structure")]
    [InlineData("decision")]
    [InlineData("price")]
    [InlineData("session")]
    [InlineData("identity")]
    public async Task Settlement_RejectsEveryRelevantLivePlanGeneration(string mutation)
    {
        var fixture = new SettlementFixture();
        switch (mutation)
        {
            case "structure":
                fixture.AppState.NotifyPlanChanged();
                break;
            case "decision":
                fixture.AppState.NotifyPlanDecisionChanged();
                break;
            case "price":
                fixture.AppState.NotifyPlanPriceChanged();
                break;
            case "session":
                fixture.AppState.ApplyBuiltRecipePlanWithActiveItems(fixture.AppState.CurrentPlan!);
                break;
            case "identity":
                fixture.AppState.TrackCurrentPlanIdentity("replacement-plan", "Replacement");
                break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Publishing));
        Assert.Empty(fixture.AppState.MarketItemAnalyses);
    }

    [Fact]
    public async Task Settlement_IndependentlyRejectsForgedRootIntentIdentity()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        var request = fixture.Request with
        {
            TransactionId = Guid.NewGuid(),
            RootIntentHash = new string('f', 64)
        };
        fixture.Registry.Register(fixture.Registration with { EngineRequest = request });

        var result = await fixture.CreateHost().ExecuteAsync(request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal("unhandled", result.Failure!.Code);
        Assert.Equal(0, fixture.Transport.ExecutionCount);
    }

    [Fact]
    public async Task Settlement_RejectsUnversionedPlanSemanticMutationAtHostRegistration()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        fixture.AppState.CurrentPlan!.RootItems[0].Name = "Mutated without notification";

        var result = await fixture.CreateHost().ExecuteAsync(fixture.Request);

        Assert.Equal(EngineTerminalStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Transport.ExecutionCount);
    }

    [Fact]
    public async Task Settlement_RejectsExactPayloadMutationThatSemanticProjectionOmits()
    {
        var fixture = new SettlementFixture();
        fixture.AppState.OnShoppingListChanged += MutatePublishedAnalysis;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Publishing));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ObserveAsync(EnginePhase.Publishing));

        void MutatePublishedAnalysis()
        {
            if (fixture.AppState.MarketItemAnalyses.Count > 0)
            {
                fixture.AppState.MarketItemAnalyses[0].LastReconciledAtUtc = DateTime.UtcNow;
            }
        }
    }

    [Fact]
    public async Task Settlement_SnapshotsMutableRegistrationPayloadBeforePublication()
    {
        var fixture = new SettlementFixture();
        fixture.Registration.PublicationRequest.Analyses[0].LastReconciledAtUtc = DateTime.UtcNow;
        fixture.Registration.PublicationRequest.ShoppingPlans[0].IconId = 999;

        await fixture.SettleAsync(EnginePhase.Publishing);

        Assert.Null(fixture.AppState.MarketItemAnalyses[0].LastReconciledAtUtc);
        Assert.Equal(0, fixture.AppState.ShoppingPlans[0].IconId);
    }

    [Fact]
    public void PublicationPayloadHash_BindsAllPublishedAndPersistedComponents()
    {
        var fixture = new SettlementFixture();
        var recipeBasis = new StoredRecipeOperationSnapshot
        {
            Operations = [new StoredRecipeOperation { NodeId = "node-1", ResultItemId = 1 }]
        };
        var snapshot = fixture.Registration.PersistenceSnapshot with { RecipeBasis = recipeBasis };
        var baseline = ComputePublicationPayloadHash(snapshot);

        Assert.NotEqual(
            baseline,
            ComputePublicationPayloadHash(snapshot with
            {
                Analyses = snapshot.Analyses
                    .Select(item => new MarketItemAnalysis
                    {
                        ItemId = item.ItemId,
                        Name = "Changed visible name",
                        QuantityNeeded = item.QuantityNeeded,
                        LastReconciledAtUtc = item.LastReconciledAtUtc
                    })
                    .ToList()
            }));
        Assert.NotEqual(
            baseline,
            ComputePublicationPayloadHash(snapshot with
            {
                ShoppingPlans = snapshot.ShoppingPlans
                    .Select(item => new DetailedShoppingPlan
                    {
                        ItemId = item.ItemId,
                        Name = item.Name,
                        QuantityNeeded = item.QuantityNeeded,
                        MarketDataWarning = "Changed persisted warning"
                    })
                    .ToList()
            }));
        Assert.NotEqual(
            baseline,
            ComputePublicationPayloadHash(snapshot with
            {
                RecipeBasis = new StoredRecipeOperationSnapshot
                {
                    Operations = [new StoredRecipeOperation { NodeId = "node-2", ResultItemId = 2 }]
                }
            }));
        Assert.NotEqual(
            baseline,
            ComputePublicationPayloadHash(snapshot with
            {
                PublishedScope = snapshot.PublishedScope with
                {
                    RequestedDataCenters = ["Primal"]
                }
            }));
    }

    [Fact]
    public void PublicationPayloadHash_BindsSettingsLensModeAndCompleteMarketIntelligence()
    {
        var fixture = new SettlementFixture();
        var snapshot = fixture.Registration.PersistenceSnapshot;
        var baseline = ComputePublicationPayloadHash(snapshot);
        var unavailable = new CoreMarketDataUnavailableItem(99, "Unavailable");

        var changes = new[]
        {
            snapshot with { SettingsVersion = snapshot.SettingsVersion + 1 },
            snapshot with
            {
                MarketIntelligence = snapshot.MarketIntelligence with
                {
                    MarketIntelligenceId = Guid.Parse("11111111-1111-1111-1111-111111111111")
                }
            },
            snapshot with
            {
                MarketIntelligence = snapshot.MarketIntelligence with
                {
                    UnavailableMarketItems = [unavailable]
                }
            },
            snapshot with
            {
                RecommendationMode = RecommendationMode.MaximizeValue,
                MarketIntelligence = snapshot.MarketIntelligence with
                {
                    PublicationContext = snapshot.MarketIntelligence.PublicationContext with
                    {
                        RecommendationMode = RecommendationMode.MaximizeValue
                    }
                }
            },
            snapshot with
            {
                MarketAnalysisLens = MarketAcquisitionLens.BulkValue,
                PublishedScope = snapshot.PublishedScope with { Lens = MarketAcquisitionLens.BulkValue },
                MarketIntelligence = snapshot.MarketIntelligence with
                {
                    PublicationContext = snapshot.MarketIntelligence.PublicationContext with
                    {
                        Lens = MarketAcquisitionLens.BulkValue
                    }
                }
            },
            snapshot with
            {
                MarketIntelligence = snapshot.MarketIntelligence with
                {
                    PublicationContext = snapshot.MarketIntelligence.PublicationContext with
                    {
                        ExpectedWorldsByDataCenter = new Dictionary<string, IReadOnlyList<string>>
                        {
                            ["Aether"] = ["Siren"]
                        }
                    }
                }
            }
        };

        Assert.All(changes, changed => Assert.NotEqual(baseline, ComputePublicationPayloadHash(changed)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settlement_RevalidatesGenerationAfterEveryPersistenceAwait(bool namedPlan)
    {
        var fixture = new SettlementFixture(namedPlan);
        if (namedPlan)
        {
            fixture.Store.OnNamedSave = fixture.AppState.NotifyPlanPriceChanged;
        }
        else
        {
            fixture.Store.OnAutoSave = fixture.AppState.NotifyPlanPriceChanged;
        }
        await fixture.SettleAsync(EnginePhase.Publishing);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Persisting));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ObserveAsync(EnginePhase.Persisting));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    public async Task Settlement_RejectsPlanIdentityChangeBeforePersistenceWithoutWriting(string mutation)
    {
        var fixture = new SettlementFixture(namedPlan: true);
        await fixture.SettleAsync(EnginePhase.Publishing);
        fixture.AppState.TrackCurrentPlanIdentity(
            mutation == "id" ? "replacement-plan" : "named-plan",
            mutation == "name" ? "Replacement plan" : "Named plan");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Persisting));

        Assert.Empty(fixture.Store.NamedSnapshots);
        Assert.Empty(fixture.Store.AutoSaveSnapshots);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("autosave")]
    public async Task Settlement_PersistenceAwaitsKeepPlanIdAndNameFromOneCapturedIdentity(string awaitedSave)
    {
        var fixture = new SettlementFixture(namedPlan: true);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (awaitedSave == "named")
        {
            fixture.Store.NamedSaveCompletion = completion;
        }
        else
        {
            fixture.Store.AutoSaveCompletion = completion;
        }
        await fixture.SettleAsync(EnginePhase.Publishing);

        var persistence = fixture.SettleAsync(EnginePhase.Persisting);
        Assert.False(persistence.IsCompleted);
        var written = awaitedSave == "named"
            ? Assert.Single(fixture.Store.NamedSnapshots)
            : Assert.Single(fixture.Store.AutoSaveSnapshots);
        Assert.Equal("named-plan", written.PlanId);
        Assert.Equal("Named plan", written.PlanName);

        fixture.AppState.TrackCurrentPlanIdentity("replacement-plan", "Replacement plan");
        completion.SetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence);
        Assert.All(
            fixture.Store.NamedSnapshots.Concat(fixture.Store.AutoSaveSnapshots),
            snapshot =>
            {
                Assert.Equal("named-plan", snapshot.PlanId);
                Assert.Equal("Named plan", snapshot.PlanName);
            });
        if (awaitedSave == "named")
        {
            Assert.Empty(fixture.Store.AutoSaveSnapshots);
        }
    }

    [Theory]
    [InlineData("named")]
    [InlineData("autosave")]
    public async Task Settlement_RejectsSettingsChangesDuringEveryPersistenceAwait(string awaitedSave)
    {
        var fixture = new SettlementFixture(namedPlan: awaitedSave == "named");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (awaitedSave == "named")
        {
            fixture.Store.NamedSaveCompletion = completion;
        }
        else
        {
            fixture.Store.AutoSaveCompletion = completion;
        }
        await fixture.SettleAsync(EnginePhase.Publishing);

        var persistence = fixture.SettleAsync(EnginePhase.Persisting);
        Assert.False(persistence.IsCompleted);
        if (awaitedSave == "named")
        {
            fixture.AppState.SetMarketAnalysisLens(MarketAcquisitionLens.BulkValue);
        }
        else
        {
            fixture.AppState.SetRecommendationMode(RecommendationMode.MaximizeValue);
        }
        completion.SetResult(true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.ObserveAsync(EnginePhase.Persisting));
    }

    [Fact]
    public async Task Settlement_PersistsOneCapturedSnapshotAfterLiveAppStateMutation()
    {
        var fixture = new SettlementFixture(namedPlan: true);
        fixture.Store.OnAutoSave = () =>
            fixture.AppState.MarketItemAnalyses[0].LastReconciledAtUtc = DateTime.UtcNow;
        await fixture.SettleAsync(EnginePhase.Publishing);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Persisting));

        var named = Assert.Single(fixture.Store.NamedSnapshots);
        var autosave = Assert.Single(fixture.Store.AutoSaveSnapshots);
        Assert.Equal(ComputePublicationPayloadHash(named), ComputePublicationPayloadHash(autosave));
        Assert.Null(named.Analyses[0].LastReconciledAtUtc);
        Assert.Equal(
            named.MarketIntelligence.MarketIntelligenceId,
            JsonSerializer.Deserialize<StoredMarketIntelligence>(autosave.ToAutoSavePlan().MarketIntelligenceJson!)!
                .MarketIntelligenceId);
    }

    [Fact]
    public async Task Settlement_RejectsUnversionedMutationAfterExecutionRegistration()
    {
        var fixture = new SettlementFixture();
        fixture.AppState.CurrentPlan!.RootItems[0].Name = "Unversioned after registration";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.SettleAsync(EnginePhase.Publishing));
        Assert.Empty(fixture.AppState.MarketItemAnalyses);
    }

    [Fact]
    public void PersistenceSnapshot_RequiresAutomaticSourceRepairBeforeRegistration()
    {
        var fixture = new SettlementFixture();
        var node = fixture.AppState.CurrentPlan!.RootItems[0];
        node.CanBuyFromVendor = true;
        node.VendorPrice = 1;
        node.Source = AcquisitionSource.MarketBuyNq;
        node.SourceReason = AcquisitionSourceReason.SystemDefault;
        var request = fixture.Registration.PublicationRequest with
        {
            ShoppingPlans =
            [
                new DetailedShoppingPlan
                {
                    ItemId = 1,
                    Name = "Material",
                    QuantityNeeded = 2,
                    Error = "No market listings"
                }
            ]
        };

        Assert.Throws<InvalidOperationException>(() =>
            fixture.PublicationService.CapturePersistenceSnapshot(request));

        var prepared = fixture.PublicationService.PrepareForRegistration(
            request,
            CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Equal(AcquisitionSource.VendorBuy, node.Source);
        Assert.Equal(1, prepared.PreparedDecisionChangeCount);
        _ = fixture.PublicationService.CapturePersistenceSnapshot(prepared);
    }

    [Fact]
    public async Task Settlement_ReplayingAllDeliveriesDoesNotRepublishOrRepersist()
    {
        var fixture = new SettlementFixture(namedPlan: true);
        var phases = new[]
        {
            EnginePhase.Publishing,
            EnginePhase.Persisting,
            EnginePhase.SettlingUi,
            EnginePhase.CapturingPostActionEvidence,
            EnginePhase.ReleasingGate
        };
        foreach (var phase in phases)
        {
            await fixture.SettleAsync(phase);
        }
        var version = fixture.AppState.CurrentVersions.MarketAnalysisVersion;

        foreach (var phase in phases)
        {
            await fixture.SettleAsync(phase);
        }

        Assert.Equal(version, fixture.AppState.CurrentVersions.MarketAnalysisVersion);
        Assert.Equal(1, fixture.Store.NamedSaveCount);
        Assert.Equal(1, fixture.Store.AutoSaveCount);
    }

    [Fact]
    public async Task HostAndAdapter_CompleteWithIdempotentGateEvidence()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        var host = fixture.CreateHost();

        var result = await host.ExecuteAsync(fixture.Request);
        var replay = await host.ExecuteAsync(fixture.Request);

        Assert.Equal(EngineTerminalStatus.Succeeded, result.Status);
        Assert.Equal(result.Completion.FinalTransactionHash, replay.Completion.FinalTransactionHash);
        Assert.Equal("operation-gate-released", result.Completion.TerminalEvidence["phase:ReleasingGate"]);
        Assert.Equal(1, fixture.Gate.ReleaseAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostAndAdapter_FalsePersistenceIsCommitIndeterminate(bool namedPlan)
    {
        var fixture = new SettlementFixture(namedPlan, hostManagedExecution: true);
        fixture.Store.ReturnFalseOnNamedSave = namedPlan;
        fixture.Store.ReturnFalseOnAutoSave = !namedPlan;

        var result = await fixture.CreateHost().ExecuteAsync(fixture.Request);

        Assert.Equal(EngineTerminalStatus.Indeterminate, result.Status);
        Assert.Equal("settlement-outcome-unknown", result.Failure!.Code);
        Assert.Equal("indeterminate", result.Completion.TerminalEvidence["commitState"]);
        Assert.False(fixture.Gate.IsHeld);
    }

    [Fact]
    public async Task HostAndAdapter_RetryRebindsHostGenerationAndPreservesSettledPhases()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        fixture.Gate.FailuresRemaining = 2;
        var host = fixture.CreateHost();

        var failed = await host.ExecuteAsync(fixture.Request);
        var retry = await host.ExecuteAsync(fixture.Request);

        Assert.Equal("gate-release-failed", failed.Failure!.Code);
        Assert.True(
            retry.Status == EngineTerminalStatus.Succeeded,
            $"{retry.Failure?.Code}: {retry.Failure?.Message}");
        Assert.Equal(2, fixture.Transport.ExecutionCount);
        Assert.Equal(2, fixture.Transport.LastGeneration);
        Assert.Equal(1, fixture.Store.AutoSaveCount);
        Assert.Equal(3, fixture.Gate.ReleaseAttempts);
    }

    [Fact]
    public async Task HostAndAdapter_PreClaimCancellationReleasesGateAndRemainsTransient()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var host = fixture.CreateHost();

        var result = await host.ExecuteAsync(fixture.Request, cancellationToken: cancellation.Token);

        Assert.Equal(EngineTerminalStatus.Cancelled, result.Status);
        Assert.Equal("unclaimed-transient", result.Completion.TerminalEvidence["replayStatus"]);
        Assert.False(fixture.Gate.IsHeld);
        Assert.Equal(0, fixture.Transport.ExecutionCount);
    }

    [Fact]
    public async Task HostAndAdapter_RegistryCapacityEvictsOnlyTerminalContexts()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true, registryCapacity: 1);
        var replacementRequest = fixture.Request with { TransactionId = Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Registry.Register(fixture.Registration with
            {
                EngineRequest = replacementRequest
            }));
        Assert.True(fixture.Gate.IsHeld);
        Assert.Equal(0, fixture.Gate.ReleaseAttempts);
        Assert.Equal(EngineTerminalStatus.Succeeded, (await fixture.CreateHost().ExecuteAsync(fixture.Request)).Status);

        fixture.Registry.Register(fixture.Registration with
        {
            EngineRequest = replacementRequest
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Settlement.ObserveAsync(
                EnginePhase.ReleasingGate,
                fixture.CreateContext(EnginePhase.ReleasingGate),
                CancellationToken.None));
    }

    [Fact]
    public void Registry_CanonicalHashFailureReleasesTheRejectedRegistrationGate()
    {
        var fixture = new SettlementFixture();
        using var malformedInput = JsonDocument.Parse("{\"value\":1e1000}");
        var request = fixture.Request with
        {
            TransactionId = Guid.NewGuid(),
            Input = malformedInput.RootElement.Clone()
        };
        var gate = new RecordingGate();
        var registration = fixture.Registration with
        {
            EngineRequest = request,
            OperationGateLease = OperationGateLease.Create(() => gate.IsHeld, gate.Release)
        };

        Assert.Throws<NotSupportedException>(() => fixture.Registry.Register(registration));

        Assert.False(gate.IsHeld);
        Assert.Equal(1, gate.ReleaseAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registry_ValidationFailureReleasesIncomingOwnedLease(bool emptyTransactionId)
    {
        var fixture = new SettlementFixture();
        var gate = new RecordingGate();
        var registration = fixture.Registration with
        {
            EngineRequest = fixture.Request with
            {
                TransactionId = emptyTransactionId ? Guid.Empty : Guid.NewGuid()
            },
            ExpectedSemanticHashes = emptyTransactionId
                ? fixture.Registration.ExpectedSemanticHashes
                : new WebMarketAnalysisExpectedSemanticHashes(string.Empty),
            OperationGateLease = OperationGateLease.Create(() => gate.IsHeld, gate.Release)
        };

        Assert.Throws<ArgumentException>(() => fixture.Registry.Register(registration));

        Assert.False(gate.IsHeld);
        Assert.Equal(1, gate.ReleaseAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registry_ValidationFailureDoesNotReleaseLeaseAlreadyOwnedByAdmission(bool emptyTransactionId)
    {
        var fixture = new SettlementFixture();
        var registration = fixture.Registration with
        {
            EngineRequest = fixture.Request with
            {
                TransactionId = emptyTransactionId ? Guid.Empty : Guid.NewGuid()
            },
            ExpectedSemanticHashes = emptyTransactionId
                ? fixture.Registration.ExpectedSemanticHashes
                : new WebMarketAnalysisExpectedSemanticHashes(string.Empty),
            OperationGateLease = fixture.GateLease.Wrap(() => fixture.Gate.IsHeld, fixture.Gate.Release)
        };

        Assert.Throws<ArgumentException>(() => fixture.Registry.Register(registration));

        Assert.True(fixture.Gate.IsHeld);
        Assert.Equal(0, fixture.Gate.ReleaseAttempts);
    }

    [Fact]
    public void Registry_RejectedCapacityAdmissionRetriesFalseGateReleaseToSuccess()
    {
        var fixture = new SettlementFixture(registryCapacity: 1);
        var gate = new RecordingGate { FailuresRemaining = 1 };
        var registration = fixture.Registration with
        {
            EngineRequest = fixture.Request with { TransactionId = Guid.NewGuid() },
            OperationGateLease = OperationGateLease.Create(() => gate.IsHeld, gate.Release)
        };

        Assert.Throws<InvalidOperationException>(() => fixture.Registry.Register(registration));

        Assert.False(gate.IsHeld);
        Assert.Equal(2, gate.ReleaseAttempts);
    }

    [Fact]
    public void Registry_PersistentRejectedGateFailureIsRetainedWithRetryEvidence()
    {
        var fixture = new SettlementFixture(registryCapacity: 1);
        var gate = new RecordingGate { FailuresRemaining = int.MaxValue };
        var registration = fixture.Registration with
        {
            EngineRequest = fixture.Request with { TransactionId = Guid.NewGuid() },
            OperationGateLease = OperationGateLease.Create(() => gate.IsHeld, gate.Release)
        };

        var failure = Assert.Throws<WebEngineRegistryAdmissionException>(() =>
            fixture.Registry.Register(registration));

        Assert.True(gate.IsHeld);
        Assert.Equal(3, gate.ReleaseAttempts);
        Assert.True(failure.CleanupEvidence.GateMayBeHeld);
        Assert.False(failure.CleanupEvidence.Released);
        Assert.Equal(3, failure.CleanupEvidence.Attempts);
        Assert.NotEqual(Guid.Empty, failure.CleanupEvidence.CleanupId);

        gate.FailuresRemaining = 0;
        var retried = fixture.Registry.RetryRejectedRegistrationGateCleanup(
            failure.CleanupEvidence.CleanupId);

        Assert.True(retried.Released);
        Assert.False(retried.GateMayBeHeld);
        Assert.Equal(4, retried.Attempts);
        Assert.False(gate.IsHeld);
    }

    [Fact]
    public void Registry_RejectedGateCleanupRetriesExceptionToSuccess()
    {
        var fixture = new SettlementFixture();
        using var malformedInput = JsonDocument.Parse("{\"value\":1e1000}");
        var gate = new RecordingGate { ExceptionsRemaining = 1 };
        var registration = fixture.Registration with
        {
            EngineRequest = fixture.Request with
            {
                TransactionId = Guid.NewGuid(),
                Input = malformedInput.RootElement.Clone()
            },
            OperationGateLease = OperationGateLease.Create(() => gate.IsHeld, gate.Release)
        };

        Assert.Throws<NotSupportedException>(() => fixture.Registry.Register(registration));

        Assert.False(gate.IsHeld);
        Assert.Equal(2, gate.ReleaseAttempts);
    }

    [Fact]
    public void Registry_ConflictUsingLeaseCallbackWrapperDoesNotReleaseAdmittedOwner()
    {
        var fixture = new SettlementFixture();
        var conflicting = fixture.Registration with
        {
            EngineRequest = fixture.Request with
            {
                Input = JsonSerializer.SerializeToElement(new { conflicting = true })
            },
            OperationGateLease = fixture.GateLease.Wrap(() => fixture.Gate.IsHeld, fixture.Gate.Release)
        };

        Assert.Throws<InvalidOperationException>(() => fixture.Registry.Register(conflicting));

        Assert.True(fixture.Gate.IsHeld);
        Assert.Equal(0, fixture.Gate.ReleaseAttempts);
    }

    [Fact]
    public void Registry_DifferentLeasesNeverAliasEvenWhenCallbacksAreEqual()
    {
        var fixture = new SettlementFixture();
        var incomingLease = OperationGateLease.Create(() => fixture.Gate.IsHeld, fixture.Gate.Release);
        var conflicting = fixture.Registration with
        {
            EngineRequest = fixture.Request with
            {
                Input = JsonSerializer.SerializeToElement(new { conflicting = true })
            },
            OperationGateLease = incomingLease
        };

        Assert.NotSame(fixture.GateLease.LeaseId, incomingLease.LeaseId);
        Assert.Throws<InvalidOperationException>(() => fixture.Registry.Register(conflicting));
        Assert.False(fixture.Gate.IsHeld);
        Assert.Equal(1, fixture.Gate.ReleaseAttempts);
    }

    [Fact]
    public async Task Registry_RetriedRejectedCleanupDoesNotReleaseLeaseAdmittedAfterRejection()
    {
        var fixture = new SettlementFixture(registryCapacity: 1);
        var gate = new RecordingGate { FailuresRemaining = int.MaxValue };
        var lease = OperationGateLease.Create(() => gate.IsHeld, gate.Release);
        var rejected = fixture.Registration with
        {
            EngineRequest = fixture.Request with { TransactionId = Guid.NewGuid() },
            OperationGateLease = lease
        };
        var failure = Assert.Throws<WebEngineRegistryAdmissionException>(() =>
            fixture.Registry.Register(rejected));
        Assert.Equal(3, gate.ReleaseAttempts);

        Assert.Equal(EngineTerminalStatus.Succeeded, (await fixture.CreateHost().ExecuteAsync(fixture.Request)).Status);
        fixture.Registry.Register(rejected);
        gate.FailuresRemaining = 0;

        var retried = fixture.Registry.RetryRejectedRegistrationGateCleanup(
            failure.CleanupEvidence.CleanupId);

        Assert.True(retried.AdmittedOwnerObserved);
        Assert.False(retried.Released);
        Assert.True(gate.IsHeld);
        Assert.Equal(3, gate.ReleaseAttempts);
    }

    [Fact]
    public async Task HostAndAdapter_ObservesPublicationWhenEventHandlerThrowsAfterMutation()
    {
        var fixture = new SettlementFixture(hostManagedExecution: true);
        fixture.AppState.OnShoppingListChanged += ThrowAfterPublication;

        var result = await fixture.CreateHost().ExecuteAsync(fixture.Request);

        Assert.True(
            result.Status == EngineTerminalStatus.Succeeded,
            $"{result.Failure?.Code}: {result.Failure?.Message}");
        Assert.StartsWith(
            "publication-observed:",
            result.Completion.TerminalEvidence["phase:Publishing"],
            StringComparison.Ordinal);
        Assert.Single(fixture.AppState.MarketItemAnalyses);

        static void ThrowAfterPublication() => throw new InvalidOperationException("UI observer failed.");
    }

    private static string ComputePublicationPayloadHash(MarketAnalysisPersistenceSnapshot snapshot)
    {
        var method = typeof(WebMarketAnalysisEngineTransactionSettlement).GetMethod(
            "ComputePublicationPayloadHash",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The internal publication hash function is unavailable.");
        return (string)method.Invoke(null, [snapshot])!;
    }

    private sealed class SettlementFixture
    {
        private readonly EngineRequestEnvelope _request;
        private readonly EngineComputationResult _computation;
        private readonly string _requestHash;
        private readonly string _invocationToken = Guid.NewGuid().ToString("N");
        private readonly string _claimToken = Guid.NewGuid().ToString("N");

        public SettlementFixture(
            bool namedPlan = false,
            bool hostManagedExecution = false,
            int registryCapacity = 128,
            bool enableTestExecution = true)
        {
            AppState = new AppState();
            AppState.SetMarketEvidenceSettings(
                "Aether",
                "North America",
                MarketFetchScope.SelectedDataCenter,
                searchEntireRegion: false);
            var plan = new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 1,
                        Name = "Material",
                        Quantity = 2,
                        Source = AcquisitionSource.MarketBuyNq,
                        CanBuyFromMarket = true
                    }
                ]
            };
            AppState.ApplyBuiltRecipePlanWithActiveItems(plan);
            if (namedPlan)
            {
                AppState.TrackCurrentPlanIdentity("named-plan", "Named plan");
            }

            AnalysisResult = CreateAnalysisResult();
            var snapshots = new ReferenceEngineSemanticSnapshotProvider();
            var semanticSnapshot = snapshots.CaptureAnalysis(AnalysisResult);
            var analysisHash = EngineSemanticSnapshotHash.Analysis(semanticSnapshot);
            var input = JsonSerializer.SerializeToElement(
                new ReferenceEngineInput(new MarketAnalysisExecutionRequest(), null),
                EngineJsonSerializerOptions.CreateWire());
            var draftRequest = new EngineRequestEnvelope(
                "1",
                Guid.NewGuid(),
                EngineInputKind.RootIntent,
                input,
                new EngineBasisSet(
                    EngineBasisIdentity.Empty("plan"),
                    EngineBasisIdentity.Empty("session"),
                    EngineBasisIdentity.Empty("publication"),
                    EngineBasisIdentity.Empty("route")),
                EngineDeterministicSettings.Default,
                EngineExecutionBudgets.Default,
                string.Empty,
                string.Empty,
                "analysis-basis",
                string.Empty);
            var preparedInput = snapshots.PrepareInput(draftRequest);
            var rootIntentHash = EngineSemanticSnapshotHash.RootIntent(preparedInput.RootIntent);
            var planSemanticHash = EngineCanonicalHash.Compute(plan, EngineJsonSerializerOptions.CreateWire());
            var versions = AppState.CurrentVersions;
            var sessionSemanticHash = EngineCanonicalHash.Compute(new
            {
                Domain = "web-app-state-session-v1",
                PlanObjectId = plan.Id,
                AppState.CurrentPlanId,
                AppState.CurrentPlanName,
                AppState.PlanSessionVersion,
                versions.PlanStructureVersion,
                versions.PlanDecisionVersion,
                versions.PlanPriceVersion,
                versions.PlanCoreVersion,
                versions.MarketAnalysisVersion,
                versions.SettingsVersion,
                AppState.RecommendationMode,
                AppState.MarketAnalysisLens,
                PlanSemanticHash = planSemanticHash,
                RootIntentHash = rootIntentHash
            });
            _request = draftRequest with
            {
                Basis = draftRequest.Basis with
                {
                    Plan = new EngineBasisIdentity("plan", "1", planSemanticHash),
                    Session = new EngineBasisIdentity("session", "1", sessionSemanticHash)
                },
                RootIntentHash = rootIntentHash,
                ExpandedGraphHash = EngineSemanticSnapshotHash.ExpandedGraph(preparedInput.ExpandedGraph)
            };
            _requestHash = EngineCanonicalHash.Compute(_request, EngineJsonSerializerOptions.CreateWire());
            var result = JsonSerializer.SerializeToElement(
                new ReferenceEngineResultSnapshot(semanticSnapshot, null),
                EngineJsonSerializerOptions.CreateWire());
            _computation = new EngineComputationResult(
                "1",
                7,
                Guid.NewGuid(),
                _request.TransactionId,
                EngineComputationStatus.Completed,
                EnginePhase.Analyzing,
                result,
                _request.Basis,
                EngineCanonicalHash.ComputeEngineInput(_request.Input),
                _request.Budgets,
                _request.RootIntentHash,
                _request.ExpandedGraphHash,
                _request.AnalysisBasisHash,
                _request.RouteBasisHash,
                analysisHash,
                string.Empty,
                "computation",
                new Dictionary<string, string>
                {
                    ["phase:Analyzing"] = "complete",
                    ["resultPayloadHash"] = EngineCanonicalHash.Compute(result)
                });
            Generation = _computation.Generation;

            Store = new RecordingStore(AppState);
            var recipeLayer = new Mock<IRecipeLayerWorkflowService>();
            recipeLayer.Setup(service => service.BuildActiveProcurementItems(plan))
                .Returns(Array.Empty<MaterialAggregate>());
            PublicationService = new MarketAnalysisPublicationService(
                AppState,
                new MarketShoppingService(Mock.Of<IMarketCacheService>()),
                Store,
                recipeLayer.Object,
                NullLogger<MarketAnalysisPublicationService>.Instance);
            Gate = new RecordingGate();
            GateLease = OperationGateLease.Create(() => Gate.IsHeld, Gate.Release);
            Registry = new WebEngineTransactionContextRegistry(registryCapacity);
            var publicationRequest = new MarketAnalysisPublicationRequest(
                plan,
                AppState.PlanSessionVersion,
                AppState.CurrentVersions.PlanDecisionVersion,
                AppState.CurrentPlanId,
                AppState.CurrentPlanName,
                AnalysisResult.Analyses,
                AnalysisResult.ShoppingPlans,
                RecipeBasis: null,
                AppState.CreateCurrentMarketAnalysisScopeSnapshot());
            var persistenceSnapshot = PublicationService.CapturePersistenceSnapshot(
                publicationRequest,
                new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc),
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
            Registration = new WebMarketAnalysisSettlementRegistration(
                _request,
                publicationRequest,
                persistenceSnapshot,
                AnalysisResult,
                new WebMarketAnalysisExpectedSemanticHashes(analysisHash),
                new WebMarketAnalysisAppStateBinding(
                    AppState.PlanSessionVersion,
                    versions.PlanStructureVersion,
                    versions.PlanDecisionVersion,
                    versions.PlanPriceVersion,
                    versions.PlanCoreVersion,
                    versions.MarketAnalysisVersion,
                    versions.SettingsVersion,
                    AppState.CurrentPlanId,
                    AppState.CurrentPlanName,
                    AppState.RecommendationMode,
                    AppState.MarketAnalysisLens,
                    planSemanticHash,
                    sessionSemanticHash,
                    rootIntentHash),
                GateLease);
            Registry.Register(Registration);
            Settlement = enableTestExecution
                ? CreateSettlementForTesting(
                    AppState,
                    Registry,
                    PublicationService,
                    snapshots)
                : new WebMarketAnalysisEngineTransactionSettlement(
                    AppState,
                    Registry,
                    PublicationService,
                    snapshots);
            if (!hostManagedExecution && enableTestExecution)
            {
                Settlement.RegisterExecutionContext(new EngineExecutionContextRegistration(
                    Generation,
                    _computation.ExecutionId,
                    _request,
                    _requestHash,
                    _invocationToken,
                    _claimToken));
            }
            Transport = new FixtureTransport(CreateHostComputation);
        }

        public AppState AppState { get; }
        public MarketAnalysisExecutionResult AnalysisResult { get; }
        public RecordingStore Store { get; }
        public MarketAnalysisPublicationService PublicationService { get; }
        public RecordingGate Gate { get; }
        public OperationGateLease GateLease { get; }
        public WebEngineTransactionContextRegistry Registry { get; }
        public WebMarketAnalysisSettlementRegistration Registration { get; }
        public WebMarketAnalysisEngineTransactionSettlement Settlement { get; }
        public FixtureTransport Transport { get; }
        public EngineRequestEnvelope Request => _request;
        public long Generation { get; set; }

        public EngineExecutionHost CreateHost() => EngineExecutionHost.CreateForTesting(
            Transport,
            Settlement,
            new InMemoryEngineTransactionLedger(),
            new ReferenceEngineSemanticSnapshotProvider());

        public Task<EngineSettlementEvidence> SettleAsync(
            EnginePhase phase,
            CancellationToken cancellationToken = default) =>
            Settlement.SettleAsync(phase, CreateContext(phase), cancellationToken);

        public Task<EngineSettlementEvidence> ObserveAsync(EnginePhase phase, long? resultGeneration = null) =>
            Settlement.ObserveAsync(
                phase,
                CreateContext(phase, resultGeneration),
                CancellationToken.None);

        public EngineSettlementContext CreateContext(EnginePhase phase, long? resultGeneration = null) =>
            new(
                _request,
                CreateHostComputation(resultGeneration ?? Generation, _computation.ExecutionId, _request),
                _requestHash,
                $"delivery:{phase}",
                _invocationToken,
                _claimToken);

        private EngineComputationResult CreateHostComputation(
            long generation,
            Guid executionId,
            EngineRequestEnvelope request)
        {
            var computation = _computation with
            {
                Generation = generation,
                ExecutionId = executionId,
                TransactionId = request.TransactionId
            };
            return computation with
            {
                ComputationHash = EngineCanonicalHash.ComputeComputationHash(
                    generation,
                    executionId,
                    request,
                    computation.Status,
                    computation.FinalPhase,
                    EngineCanonicalHash.Compute(computation.Result!.Value),
                    computation.AnalysisResultHash,
                    computation.ProcurementRouteResultHash,
                    computation.ComputationEvidence,
                    computation.Failure)
            };
        }

        private static MarketAnalysisExecutionResult CreateAnalysisResult()
        {
            return new MarketAnalysisExecutionResult(
                new MarketEvidenceSet(
                    new Dictionary<(int itemId, string dataCenter), CachedMarketData>(),
                    [(1, "Aether")],
                    MarketFetchScope.SelectedDataCenter,
                    ["Aether"],
                    "Aether",
                    "North America",
                    maxAge: null,
                    fetchedCount: 1,
                    DateTime.UtcNow),
                [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 2 }],
                [
                    new DetailedShoppingPlan
                    {
                        ItemId = 1,
                        Name = "Material",
                        QuantityNeeded = 2,
                        RecommendedWorld = new WorldShoppingSummary
                        {
                            DataCenter = "Aether",
                            WorldName = "Siren",
                            TotalQuantityPurchased = 2,
                            TotalCost = 20
                        }
                    }
                ]);
        }

        private static WebMarketAnalysisEngineTransactionSettlement CreateSettlementForTesting(
            AppState appState,
            WebEngineTransactionContextRegistry registry,
            MarketAnalysisPublicationService publicationService,
            IReferenceEngineSemanticSnapshotProvider snapshots)
        {
            var factory = typeof(WebMarketAnalysisEngineTransactionSettlement).GetMethod(
                "CreateForTesting",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("The internal Web settlement test factory is unavailable.");
            return (WebMarketAnalysisEngineTransactionSettlement)factory.Invoke(
                null,
                [appState, registry, publicationService, snapshots])!;
        }
    }

    private sealed class RecordingStore(AppState appState) : IMarketAnalysisPublicationStore
    {
        public List<string> Calls { get; } = [];
        public List<MarketAnalysisPersistenceSnapshot> NamedSnapshots { get; } = [];
        public List<MarketAnalysisPersistenceSnapshot> AutoSaveSnapshots { get; } = [];
        public int NamedSaveCount { get; private set; }
        public int AutoSaveCount { get; private set; }
        public bool ThrowOnAutoSave { get; set; }
        public bool ReturnFalseOnNamedSave { get; set; }
        public bool ReturnFalseOnAutoSave { get; set; }
        public TaskCompletionSource<bool>? NamedSaveCompletion { get; set; }
        public TaskCompletionSource<bool>? AutoSaveCompletion { get; set; }
        public Action? OnNamedSave { get; set; }
        public Action? OnAutoSave { get; set; }

        public Task<bool> SaveNamedPlanAsync(MarketAnalysisPersistenceSnapshot snapshot)
        {
            Assert.Single(appState.MarketItemAnalyses);
            Calls.Add("named");
            NamedSnapshots.Add(snapshot);
            NamedSaveCount++;
            OnNamedSave?.Invoke();
            return NamedSaveCompletion?.Task ?? Task.FromResult(!ReturnFalseOnNamedSave);
        }

        public Task<bool> AutoSaveAsync(MarketAnalysisPersistenceSnapshot snapshot)
        {
            Assert.Single(appState.MarketItemAnalyses);
            Calls.Add("autosave");
            AutoSaveSnapshots.Add(snapshot);
            AutoSaveCount++;
            OnAutoSave?.Invoke();
            if (AutoSaveCompletion is not null)
            {
                return AutoSaveCompletion.Task;
            }
            return ThrowOnAutoSave
                ? Task.FromException<bool>(new IOException("Autosave acknowledgement was lost."))
                : Task.FromResult(!ReturnFalseOnAutoSave);
        }
    }

    private sealed class RecordingGate
    {
        public bool IsHeld { get; private set; } = true;
        public int FailuresRemaining { get; set; }
        public int ExceptionsRemaining { get; set; }
        public int ReleaseAttempts { get; private set; }

        public bool Release()
        {
            ReleaseAttempts++;
            if (ExceptionsRemaining > 0)
            {
                ExceptionsRemaining--;
                throw new IOException("Gate release acknowledgement failed.");
            }
            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                return false;
            }
            IsHeld = false;
            return true;
        }
    }

    private sealed class FixtureTransport(
        Func<long, Guid, EngineRequestEnvelope, EngineComputationResult> createResult)
        : IEngineExecutionTransport
    {
        public EngineExecutionTransportCapability Capability { get; } =
            new(EngineExecutionTransportKind.InProcess, true);
        public int ExecutionCount { get; private set; }
        public long LastGeneration { get; private set; }

        public Task<EngineComputationResult> ExecuteAsync(
            long generation,
            Guid executionId,
            EngineRequestEnvelope request,
            IProgress<EngineProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            LastGeneration = generation;
            return Task.FromResult(createResult(generation, executionId, request));
        }
    }
}
