using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CommissionProjectionStreamContractTests
{
    [Fact]
    public void ProjectionTagsAreDeterministicAndAudienceSpecific()
    {
        var publicProjection = new
        {
            publicBriefId = "commission-test",
            projectionRevision = 7,
            status = "InProgress"
        };
        var participantProjection = new
        {
            publicBriefId = "commission-test",
            projectionRevision = 7,
            status = "InProgress",
            participantCapabilityRevision = 3
        };

        var first = CommissionProjectionTag.Create(publicProjection);
        var repeat = CommissionProjectionTag.Create(publicProjection);
        var participant = CommissionProjectionTag.Create(participantProjection);

        Assert.True(CommissionProjectionTag.IsValid(first));
        Assert.Equal(first, repeat);
        Assert.NotEqual(first, participant);
    }

    [Fact]
    public async Task ProjectionSignalWakesCurrentObserverOnly()
    {
        var signal = new CommissionProjectionChangeSignal();
        var current = signal.Observe("commission-test");

        signal.Publish("commission-test");
        await current.Changed.WaitAsync(TimeSpan.FromSeconds(1));
        var successor = signal.Observe("commission-test");

        Assert.Equal(0, current.Generation);
        Assert.Equal(1, successor.Generation);
        Assert.False(successor.Changed.IsCompleted);
    }

    [Fact]
    public void CommissionClientUsesHeaderAuthenticatedProjectionTagStream()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var client = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FFXIV Craft Architect.Web",
            "wwwroot",
            "commission-client.js"));

        Assert.Contains("/stream?projectionTag=", client, StringComparison.Ordinal);
        Assert.Contains("headers[\"X-Commission-Participant\"]", client, StringComparison.Ordinal);
        Assert.Contains("eventName !== \"commission-projection\"", client, StringComparison.Ordinal);
        Assert.Contains("await onProjectionChanged(nextTag)", client, StringComparison.Ordinal);
        Assert.DoesNotContain("new EventSource", client, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerCommandResponseCarriesCompleteCommittedProjection()
    {
        var order = new TradeOrder
        {
            Id = Guid.NewGuid(),
            CompanyProfileId = Guid.NewGuid(),
            Title = "Committed commission"
        };
        var projection = new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(12),
            CompanyRevision = new CompanyRecordRevision(21)
        };
        var response = new TradeCommissionOwnerCommandResponse(
            CompanyCommissionMutationStatus.Applied,
            order,
            Activity: null,
            ErrorCode: null,
            ErrorMessage: null,
            Projection: projection);

        var json = JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"projection\"", json, StringComparison.Ordinal);
        Assert.Contains("\"objectRevision\":12", json, StringComparison.Ordinal);
        Assert.Contains("\"companyRevision\":21", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalCommittedRevisionsDoNotLeakThroughParticipantMutationShape()
    {
        var mutation = new CompanyCommissionMutationResult(
            CompanyCommissionMutationStatus.Applied,
            new TradeOrder
            {
                Id = Guid.NewGuid(),
                CompanyProfileId = Guid.NewGuid(),
                Title = "Participant mutation"
            },
            ObjectRevision: new CompanyRecordRevision(12),
            CompanyRevision: new CompanyRecordRevision(21));

        var json = JsonSerializer.Serialize(
            mutation,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("objectRevision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("companyRevision", json, StringComparison.OrdinalIgnoreCase);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
