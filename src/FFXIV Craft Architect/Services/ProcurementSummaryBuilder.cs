using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Services.Interfaces;

namespace FFXIV_Craft_Architect.Services;

/// <summary>
/// Implementation of IProcurementSummaryBuilder for creating procurement plan summaries.
/// </summary>
public class ProcurementSummaryBuilder : IProcurementSummaryBuilder
{
    /// <inheritdoc />
    public void BuildSummary(Panel targetPanel, IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        targetPanel.Children.Clear();
        
        var plans = shoppingPlans?.ToList();
        if (plans?.Any() != true)
            return;
        
        var worldCards = ProcurementWorldCardBuilder
            .BuildWorldCards(plans, string.Empty)
            .ToList();
        
        if (!worldCards.Any())
        {
            targetPanel.Children.Add(new TextBlock 
            { 
                Text = "No viable market listings found",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12
            });
            return;
        }
        
        foreach (var worldCard in worldCards)
        {
            var worldName = GetWorldDisplayName(worldCard.WorldName, worldCard.DataCenter);
            var isHomeWorld = worldCard.Items.First().SourcePlan?.RecommendedWorld?.IsHomeWorld ?? false;
            
            var worldHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            
            var worldText = new TextBlock
            {
                Text = worldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = isHomeWorld
                    ? ResolveBrush("AccentGoldBrush", Brushes.Gold)
                    : ResolveBrush("TextPrimaryBrush", Brushes.White)
            };
            worldHeader.Children.Add(worldText);
            
            if (isHomeWorld)
            {
                worldHeader.Children.Add(new TextBlock
                {
                    Text = " \u2605 HOME",
                    Foreground = ResolveBrush("AccentGoldBrush", Brushes.Gold),
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }
            
            worldHeader.Children.Add(new TextBlock
            {
                Text = $" - {worldCard.ItemCount} items, {worldCard.TotalCost:N0}g total",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            
            targetPanel.Children.Add(worldHeader);
            
            foreach (var item in worldCard.Items.OrderBy(i => i.ItemName))
            {
                var itemText = new TextBlock
                {
                    Text = $"  \u2022 {item.ItemName} {GetQuantityDisplay(item)} = {item.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                targetPanel.Children.Add(itemText);
            }
            
            targetPanel.Children.Add(new Border { Height = 12 });
        }
        
        var grandTotal = worldCards.Sum(w => w.TotalCost);
        var totalText = new TextBlock
        {
            Text = $"Grand Total: {grandTotal:N0}g",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = ResolveBrush("Brush.Status.Success", Brushes.LightGreen),
            Margin = new Thickness(0, 8, 0, 0)
        };
        targetPanel.Children.Add(totalText);
    }

    private static Brush ResolveBrush(string resourceKey, Brush fallback)
    {
        return Application.Current?.TryFindResource(resourceKey) as Brush ?? fallback;
    }

    private static string GetWorldDisplayName(string worldName, string dataCenter)
    {
        if (string.Equals(worldName, MarketShoppingConstants.VendorWorldName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(dataCenter))
        {
            return worldName;
        }

        return $"{worldName} ({dataCenter})";
    }

    private static string GetQuantityDisplay(WorldItemPurchase item)
    {
        return item.IsSplitPurchase ? item.QuantityDisplay : item.SimpleQuantityDisplay;
    }
}
