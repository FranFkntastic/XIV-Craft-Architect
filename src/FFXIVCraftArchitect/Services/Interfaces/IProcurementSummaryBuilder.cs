using System.Windows.Controls;
using FFXIVCraftArchitect.Core.Models;

namespace FFXIVCraftArchitect.Services.Interfaces;

/// <summary>
/// Builds the procurement plan summary panel showing items grouped by world.
/// </summary>
public interface IProcurementSummaryBuilder
{
    /// <summary>
    /// Builds a summary panel displaying shopping plans grouped by recommended world.
    /// </summary>
    /// <param name="targetPanel">The panel to populate with the summary.</param>
    /// <param name="shoppingPlans">The shopping plans to summarize.</param>
    void BuildSummary(Panel targetPanel, IEnumerable<DetailedShoppingPlan> shoppingPlans);
}
