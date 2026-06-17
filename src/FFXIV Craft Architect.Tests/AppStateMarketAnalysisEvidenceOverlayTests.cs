using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public class AppStateMarketAnalysisEvidenceOverlayTests
{
    [Fact]
    public void MarketAnalysisEvidenceOverlay_DefaultsToCompetitivenessAndUpdatesTransientState()
    {
        var appState = new AppState();

        Assert.Equal(MarketAnalysisEvidenceOverlay.CompetitivenessOverlay, appState.MarketAnalysisEvidenceOverlay);
        Assert.True(appState.SetMarketAnalysisEvidenceOverlay(MarketAnalysisEvidenceOverlay.PriceBandOverlay));
        Assert.Equal(MarketAnalysisEvidenceOverlay.PriceBandOverlay, appState.MarketAnalysisEvidenceOverlay);
        Assert.False(appState.SetMarketAnalysisEvidenceOverlay(MarketAnalysisEvidenceOverlay.PriceBandOverlay));
    }
}
