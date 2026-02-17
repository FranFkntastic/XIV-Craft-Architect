using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Models;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect.UIBuilders;

/// <summary>
/// Builds WPF UI elements for the shopping list.
/// Separates UI creation from view logic.
/// </summary>
public class ShoppingListBuilder
{
    private readonly ILogger<ShoppingListBuilder>? _logger;

    // Color constants
    private static readonly Brush NameForeground = Brushes.White;
    private static readonly Brush QuantityForeground = Brushes.LightGray;
    private static readonly Brush PriceForeground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50"));
    private static readonly Brush AlternateRowBackground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333333"));

    public ShoppingListBuilder(ILogger<ShoppingListBuilder>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Populates the target panel with shopping list rows.
    /// </summary>
    public void PopulatePanel(Panel targetPanel, IEnumerable<MaterialAggregate> materials)
    {
        targetPanel.Children.Clear();

        bool isAlternate = false;
        foreach (var material in materials.OrderBy(m => m.Name))
        {
            var row = CreateShoppingListRow(material, isAlternate);
            targetPanel.Children.Add(row);
            isAlternate = !isAlternate;
        }
    }

    /// <summary>
    /// Calculates total cost from materials.
    /// </summary>
    public decimal CalculateTotalCost(IEnumerable<MaterialAggregate> materials)
    {
        return materials.Sum(m => m.UnitPrice > 0 ? m.UnitPrice * m.TotalQuantity : 0);
    }

    private Grid CreateShoppingListRow(MaterialAggregate material, bool isAlternateRow)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

        var nameBlock = new TextBlock
        {
            Text = material.Name,
            Foreground = NameForeground,
            Padding = new Thickness(12, 6, 4, 6),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(nameBlock, 0);
        row.Children.Add(nameBlock);

        var qtyBlock = new TextBlock
        {
            Text = material.TotalQuantity.ToString(),
            Foreground = QuantityForeground,
            Padding = new Thickness(4, 6, 4, 6),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(qtyBlock, 1);
        row.Children.Add(qtyBlock);

        var price = material.UnitPrice > 0 ? material.UnitPrice * material.TotalQuantity : 0;
        var priceText = price > 0 ? $"{price:N0}g" : "-";
        var priceBlock = new TextBlock
        {
            Text = priceText,
            Foreground = PriceForeground,
            Padding = new Thickness(4, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetColumn(priceBlock, 2);
        row.Children.Add(priceBlock);

        if (isAlternateRow)
        {
            row.Background = AlternateRowBackground;
        }

        return row;
    }
}
