using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.Services.Interfaces;

namespace FFXIVCraftArchitect.Services;

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
        
        var itemsByWorld = plans
            .Where(p => p.RecommendedWorld != null)
            .GroupBy(p => p.RecommendedWorld!.WorldName)
            .OrderBy(g => g.Key)
            .ToList();
        
        if (!itemsByWorld.Any())
        {
            targetPanel.Children.Add(new TextBlock 
            { 
                Text = "No viable market listings found",
                Foreground = Brushes.Gray,
                FontSize = 12
            });
            return;
        }
        
        foreach (var worldGroup in itemsByWorld)
        {
            var worldName = worldGroup.Key;
            var items = worldGroup.ToList();
            var worldTotal = items.Sum(i => i.RecommendedWorld?.TotalCost ?? 0);
            var isHomeWorld = items.First().RecommendedWorld?.IsHomeWorld ?? false;
            
            var worldHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            
            var worldText = new TextBlock
            {
                Text = worldName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = isHomeWorld ? Brushes.Gold : Brushes.White
            };
            worldHeader.Children.Add(worldText);
            
            if (isHomeWorld)
            {
                worldHeader.Children.Add(new TextBlock
                {
                    Text = " \u2605 HOME",
                    Foreground = Brushes.Gold,
                    FontSize = 10,
                    FontWeight = FontWeights.Bold,
                    Margin = new Thickness(4, 0, 0, 0)
                });
            }
            
            worldHeader.Children.Add(new TextBlock
            {
                Text = $" - {items.Count} items, {worldTotal:N0}g total",
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            });
            
            targetPanel.Children.Add(worldHeader);
            
            foreach (var item in items.OrderBy(i => i.Name))
            {
                var itemText = new TextBlock
                {
                    Text = $"  \u2022 {item.Name} \u00d7{item.QuantityNeeded} = {item.RecommendedWorld?.TotalCost:N0}g",
                    FontSize = 11,
                    Foreground = Brushes.LightGray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 2)
                };
                targetPanel.Children.Add(itemText);
            }
            
            targetPanel.Children.Add(new Border { Height = 12 });
        }
        
        var grandTotal = plans.Sum(p => p.RecommendedWorld?.TotalCost ?? 0);
        var totalText = new TextBlock
        {
            Text = $"Grand Total: {grandTotal:N0}g",
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            Margin = new Thickness(0, 8, 0, 0)
        };
        targetPanel.Children.Add(totalText);
    }
}
