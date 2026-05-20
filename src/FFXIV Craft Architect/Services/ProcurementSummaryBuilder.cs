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
    private static readonly PurchaseSummaryService _summaryService = new();
    
    /// <inheritdoc />
    public void BuildSummary(Panel targetPanel, IEnumerable<DetailedShoppingPlan> shoppingPlans)
    {
        targetPanel.Children.Clear();
        
        var plans = shoppingPlans?.ToList();
        if (plans?.Any() != true)
            return;
        
        var summaries = _summaryService.CreateSummaries(plans);
        
        var itemsByWorld = summaries
            .Where(s => s.RecommendedWorld != null)
            .GroupBy(s => s.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .ToList();
        
        if (!itemsByWorld.Any())
        {
            targetPanel.Children.Add(new TextBlock 
            { 
                Text = "No viable market listings found",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 12
            });
            return;
        }
        
        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.TotalCost);
            var isHomeWorld = items.First().RecommendedWorld?.IsHomeWorld ?? false;
            
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
                Text = $" - {items.Count} items, {worldTotal:N0}g total",
                Foreground = ResolveBrush("GrayBrush", Brushes.Gray),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            
            targetPanel.Children.Add(worldHeader);
            
            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  \u2022 {item.ShortDisplayText} = {item.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = ResolveBrush("LightGrayBrush", Brushes.LightGray),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                targetPanel.Children.Add(itemText);
            }
            
            targetPanel.Children.Add(new Border { Height = 12 });
        }
        
        var grandTotal = summaries.Sum(s => s.TotalCost);
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
}
