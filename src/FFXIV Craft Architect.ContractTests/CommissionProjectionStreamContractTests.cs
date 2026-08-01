using FFXIV_Craft_Architect.LodestoneLookup.Services.CommissionBriefs;

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
