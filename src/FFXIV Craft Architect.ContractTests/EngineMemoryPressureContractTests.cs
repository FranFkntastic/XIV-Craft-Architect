using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class EngineMemoryPressureContractTests
{
    [Fact]
    public async Task CanonicalHash_StreamsTheSameKnownCanonicalPayload()
    {
        using var document = JsonDocument.Parse("""
            {"z":"\u0061","a":1.2300}
            """);
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("{\"a\":1.23,\"z\":\"a\"}")))
            .ToLowerInvariant();

        var synchronous = EngineCanonicalHash.ComputeEngineInput(document.RootElement);
        var cooperative = await EngineCanonicalHash.ComputeEngineInputAsync(
            document.RootElement,
            _ => ValueTask.CompletedTask);

        Assert.Equal(expected, synchronous);
        Assert.Equal(expected, cooperative);
        Assert.Equal(expected, EngineCanonicalHash.Compute(document.RootElement));
    }

    [Fact]
    public async Task EngineMemoryPressureLease_WaitsForPersistenceAndSuppressesRoutineAutosave()
    {
        var state = new AppState();
        state.ApplyBuiltRecipePlan(
            new CraftingPlan
            {
                RootItems =
                [
                    new PlanNode
                    {
                        ItemId = 1,
                        Name = "Test Item",
                        Quantity = 1,
                    },
                ],
            },
            []);
        var inFlightSave = Assert.IsType<AppStateAutoSaveLease>(
            await state.BeginAutoSaveAsync());

        var engineLeaseTask = state.BeginEngineMemoryPressureLeaseAsync();
        Assert.False(engineLeaseTask.IsCompleted);

        state.CompleteAutoSave(
            succeeded: false,
            inFlightSave.CapturedVersions,
            inFlightSave.DirtyBuckets);
        using var engineLease = await engineLeaseTask;

        Assert.Null(await state.BeginAutoSaveAsync());
        var authoritativeSave = Assert.IsType<AppStateAutoSaveLease>(
            await state.BeginAutoSaveAsync(allowDuringEngineMemoryPressure: true));
        state.CompleteAutoSave(
            succeeded: false,
            authoritativeSave.CapturedVersions,
            authoritativeSave.DirtyBuckets);

        engineLease.Dispose();
        var resumedSave = Assert.IsType<AppStateAutoSaveLease>(
            await state.BeginAutoSaveAsync());
        state.CompleteAutoSave(
            succeeded: false,
            resumedSave.CapturedVersions,
            resumedSave.DirtyBuckets);
    }
}
