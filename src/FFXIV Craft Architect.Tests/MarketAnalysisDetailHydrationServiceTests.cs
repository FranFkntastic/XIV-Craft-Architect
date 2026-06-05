using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class MarketAnalysisDetailHydrationServiceTests
{
    [Fact]
    public async Task LoadWorldDetailAsync_WhenColdDetailExists_ReturnsListingDetails()
    {
        var publicationId = Guid.NewGuid();
        var store = new InMemoryMarketIntelligenceStore();
        var world = new MarketWorldKey("Aether", "Siren");
        var listing = new AnalyzedMarketListing
        {
            SortIndex = 0,
            Quantity = 2,
            PricePerUnit = 100,
            RetainerName = "Test Retainer"
        };
        var detailKey = new MarketIntelligenceDetailKey(
            publicationId,
            MarketFetchScope.SelectedDataCenter,
            1,
            world,
            new MarketDemandFingerprint("item:1"));
        await store.SavePublicationAsync(new MarketIntelligencePublicationWrite(
            new MarketIntelligencePublicationSummary
            {
                PublicationId = publicationId,
                DetailManifest = new MarketIntelligenceDetailManifest
                {
                    PublicationId = publicationId,
                    Entries =
                    [
                        new MarketIntelligenceDetailManifestEntry
                        {
                            Key = detailKey,
                            Availability = MarketIntelligenceDetailAvailability.Available,
                            ListingCount = 1
                        }
                    ]
                }
            },
            [
                new MarketListingDetail
                {
                    Key = detailKey,
                    Listings = [listing]
                }
            ],
            []));
        var service = new MarketAnalysisDetailHydrationService(store);

        var result = await service.LoadWorldDetailAsync(
            publicationId,
            1,
            new WorldMarketAnalysis
            {
                DataCenter = "Aether",
                WorldName = "Siren"
            });

        Assert.True(result.HasListings);
        Assert.False(result.FromEmbeddedHotState);
        Assert.Equal("Test Retainer", Assert.Single(result.Listings).RetainerName);
    }

    [Fact]
    public async Task LoadWorldDetailAsync_WithoutPublication_ReturnsSummaryOnlyState()
    {
        var service = new MarketAnalysisDetailHydrationService(new InMemoryMarketIntelligenceStore());

        var result = await service.LoadWorldDetailAsync(
            null,
            1,
            new WorldMarketAnalysis
            {
                DataCenter = "Aether",
                WorldName = "Siren"
            });

        Assert.False(result.HasListings);
        Assert.Equal(MarketAnalysisWorldDetailHydrationStatus.SummaryOnly, result.Status);
        Assert.Contains("summary", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
