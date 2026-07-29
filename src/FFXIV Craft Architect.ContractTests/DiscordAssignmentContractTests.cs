using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class DiscordAssignmentContractTests
{
    [Fact]
    public async Task OperatorConfirmation_CommitsAssignmentStatusAndHistoryInOneOrderMutation()
    {
        var companyId = new CompanyId(
            Guid.Parse("018fdc85-9b7a-7c31-87ed-6f9bdb4a7777"));
        var companyProfileId = Guid.Parse("3cda70f4-daee-4f71-b446-9fc02574f136");
        var order = new TradeOrder
        {
            Id = Guid.Parse("cc58c224-d6e6-402b-bcdd-e7b45dd00b77"),
            CompanyProfileId = companyProfileId,
            Status = TradeOrderStatus.ReadyToAssign
        };
        var crafter = new TradeCrafterProfile
        {
            Id = Guid.Parse("9187e2bd-f941-4e3f-82e4-04507c461277"),
            CompanyProfileId = companyProfileId,
            DisplayName = "Confirmed Crafter"
        };
        var company = new RecordingCompanyService(
            companyId,
            Envelope(companyId, TradeCompanyRecordKinds.Order, order.Id, order, 7, 10),
            Envelope(companyId, TradeCompanyRecordKinds.Crafter, crafter.Id, crafter, 3, 10));
        var adapter = new DiscordCompanyOrderAdapter(company);
        var access = new TradeCompanyAccessContext(
            companyId,
            Guid.Parse("c0ad3aef-868d-458c-870a-c7ba7665566f"),
            TradeCompanyRole.Operator);

        var currentOrder = await adapter.LoadOrderAsync(access, order.Id);
        var selectedCrafter = await adapter.LoadCrafterAsync(access, crafter.Id);
        var result = await adapter.AssignAsync(
            access,
            currentOrder!,
            selectedCrafter!,
            "discord-accept-01",
            DateTime.UnixEpoch);

        Assert.True(result.Success);
        var mutation = Assert.IsType<TradeCompanyMutationRequest>(company.LastMutation);
        Assert.Equal(TradeCompanyRecordKinds.Order, mutation.RecordKind);
        Assert.Equal(new CompanyRecordRevision(7), mutation.ExpectedRecordRevision);
        Assert.Equal(new CompanyRevision(10), mutation.ExpectedCompanyRevision);
        Assert.Equal("discord-accept-01", mutation.IdempotencyKey);
        var updated = JsonSerializer.Deserialize<TradeOrder>(
            mutation.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(updated);
        Assert.Equal(crafter.Id, updated.AssignedCrafterId);
        Assert.Equal(TradeOrderStatus.Assigned, updated.Status);
        Assert.Contains(
            updated.History,
            entry =>
                entry.Kind == TradeOrderHistoryEventKind.Assigned &&
                entry.CrafterId == crafter.Id);
        Assert.Contains(
            updated.History,
            entry =>
                entry.Kind == TradeOrderHistoryEventKind.StatusChanged &&
                entry.FromStatus == TradeOrderStatus.ReadyToAssign &&
                entry.ToStatus == TradeOrderStatus.Assigned);
    }

    private static TradeCompanyRecordEnvelope Envelope<T>(
        CompanyId companyId,
        string kind,
        Guid id,
        T payload,
        long recordRevision,
        long companyRevision) =>
        new(
            companyId,
            kind,
            id.ToString("D"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            new CompanyRecordRevision(recordRevision),
            new CompanyRevision(companyRevision),
            DateTime.UnixEpoch);

    private sealed class RecordingCompanyService(
        CompanyId companyId,
        params TradeCompanyRecordEnvelope[] records) : ITradeCompanyService
    {
        public TradeCompanyMutationRequest? LastMutation { get; private set; }

        public Task<TradeCompanyIdentity?> GetCompanyAsync(
            TradeCompanyAccessContext access,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TradeCompanyIdentity?>(null);

        public Task<TradeCompanyChangeSet> GetChangesAsync(
            TradeCompanyAccessContext access,
            CompanyRevision afterRevision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TradeCompanyChangeSet(
                companyId,
                new CompanyRevision(10),
                records));

        public Task<TradeCompanyMutationResult> MutateAsync(
            TradeCompanyAccessContext access,
            TradeCompanyMutationRequest request,
            CancellationToken cancellationToken = default)
        {
            LastMutation = request;
            var applied = new TradeCompanyRecordEnvelope(
                request.CompanyId,
                request.RecordKind,
                request.RecordId,
                request.PayloadJson,
                request.ExpectedRecordRevision.Value == long.MaxValue
                    ? request.ExpectedRecordRevision
                    : new CompanyRecordRevision(request.ExpectedRecordRevision.Value + 1),
                request.ExpectedCompanyRevision.Next(),
                DateTime.UnixEpoch);
            return Task.FromResult(new TradeCompanyMutationResult(
                TradeCompanyMutationStatus.Applied,
                applied));
        }

        public Task<TradeCompanyPublicationOwnership?> ResolvePublicationOwnershipAsync(
            string publicId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TradeCompanyPublicationOwnership?>(null);
    }
}
