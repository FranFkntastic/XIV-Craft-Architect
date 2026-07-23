using System.Text.Json;
using CraftArchitectEngineWorker;
using FFXIV_Craft_Architect.Core.Engine;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkerSessionContractTests
{
    private static readonly JsonSerializerOptions WireOptions =
        EngineJsonSerializerOptions.CreateWire();

    [Fact]
    public async Task RestorePublishesOneRevisionAndRejectsStaleReaders()
    {
        var plan = new CraftingPlan
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Worker plan",
            DataCenter = "Aether",
            RootItems =
            [
                new PlanNode
                {
                    NodeId = "root",
                    ItemId = 42,
                    Name = "Root",
                    Quantity = 2,
                    Children =
                    [
                        new PlanNode
                        {
                            NodeId = "child",
                            ItemId = 43,
                            Name = "Child",
                            Quantity = 4
                        }
                    ]
                }
            ]
        };
        var storedPlan = new StoredPlan
        {
            Id = "autosave",
            Name = "Autosave",
            DataCenter = "Aether",
            PlanJson = JsonSerializer.Serialize(plan),
            ProjectItems =
            [
                new StoredProjectItem { Id = 42, Name = "Root", Quantity = 2 }
            ]
        };

        var restored = await SendAsync(
            "restore",
            expectedRevision: 0,
            new WorkerSessionRestorePayload(
                Revision: 1,
                storedPlan,
                TrackStoredPlanIdentity: false,
                MigratedFromLegacy: true));

        Assert.True(restored.Accepted);
        Assert.Equal(1, restored.Revision);
        var shell = restored.Projection.Deserialize<WorkerSessionShellProjection>(WireOptions);
        Assert.NotNull(shell);
        Assert.True(shell.HasSession);
        Assert.Equal("Worker plan", shell.PlanName);
        Assert.Equal(2, shell.PlanNodeCount);
        Assert.True(shell.MigratedFromLegacy);

        var stale = await SendAsync("shell", expectedRevision: 0, new { });
        Assert.False(stale.Accepted);
        Assert.Equal("stale-revision", stale.RejectionCode);
        Assert.Equal(1, stale.Revision);

        var exported = await SendAsync(
            "export",
            expectedRevision: 1,
            new WorkerSessionExportRequest(
                "autosave",
                "Autosave",
                IncludeSourcePlanIdentity: true,
                IncludeLegacyMarketAnalysisFields: false));
        Assert.True(exported.Accepted);
        var export = exported.Projection.Deserialize<WorkerSessionExportProjection>(WireOptions);
        Assert.NotNull(export);
        Assert.Equal(1, export.Revision);
        Assert.NotNull(export.StoredPlan?.PlanJson);
    }

    private static async Task<WorkerSessionResultEnvelope> SendAsync<TPayload>(
        string commandKind,
        long expectedRevision,
        TPayload payload)
    {
        var commandId = Guid.NewGuid();
        var command = new WorkerSessionCommandEnvelope(
            WorkerSessionProtocol.ContractVersion,
            commandKind,
            expectedRevision,
            JsonSerializer.SerializeToElement(payload, WireOptions));
        var message = new EngineWorkerMessage(
            EngineWorkerClient.ProtocolVersion,
            WorkerSessionProtocol.CommandMessageKind,
            1,
            commandId,
            commandId,
            JsonSerializer.SerializeToElement(command, WireOptions));
        var responseJson = await ManagedHost.ExecuteSessionCommandJsonCore(
            JsonSerializer.Serialize(message, WireOptions));
        var response = JsonSerializer.Deserialize<EngineWorkerMessage>(responseJson, WireOptions);
        return response?.Payload?.Deserialize<WorkerSessionResultEnvelope>(WireOptions)
            ?? throw new InvalidOperationException("Worker session response was empty.");
    }
}
