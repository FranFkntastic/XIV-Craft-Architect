using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class PackagedWorldDirectoryServiceTests
{
    [Fact]
    public void LoadSnapshot_ReturnsValidatedReleaseDirectory()
    {
        var service = new PackagedWorldDirectoryService();

        var snapshot = service.LoadSnapshot();

        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.True(snapshot.WorldIdToName.Count >= 128);
        Assert.True(snapshot.DataCenterToWorlds.Count >= 18);
        Assert.Equal("Excalibur", snapshot.WorldIdToName[93]);
        Assert.Contains("Halicarnassus", snapshot.DataCenterToWorlds["Dynamis"]);
    }

    [Fact]
    public async Task InitializeWorldDataAsync_SeedsUniversalisWithoutNetworkAccess()
    {
        using var httpClient = new HttpClient(new RejectNetworkHandler());
        var universalis = new UniversalisService(httpClient);
        var appState = new AppState();

        await appState.InitializeWorldDataAsync(new PackagedWorldDirectoryService(), universalis);

        Assert.NotNull(appState.WorldData);
        Assert.Same(appState.WorldData, universalis.GetCachedWorldData());
        Assert.Equal("Excalibur", appState.WorldData.WorldIdToName[93]);
    }

    [Fact]
    public async Task UniversalisWorldDirectory_UsesPackagedSnapshotWithoutNetworkAccess()
    {
        using var httpClient = new HttpClient(new RejectNetworkHandler());
        var universalis = new UniversalisService(httpClient);

        var worldData = await universalis.GetWorldDataAsync();

        Assert.Equal("Excalibur", worldData.WorldIdToName[93]);
        Assert.Contains("Halicarnassus", worldData.DataCenterToWorlds["Dynamis"]);
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException($"Unexpected network request: {request.RequestUri}");
        }
    }
}
