using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FFXIV_Craft_Architect.Core.Helpers;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.UIBuilders;

/// <summary>
/// Builds WPF UI elements for the recipe tree.
/// Separates UI creation from view logic.
/// </summary>
public class RecipeTreeUiBuilder
{
    private readonly Dictionary<string, NodeUiElements> _nodeUiRegistry = new();
    private readonly Action<string, AcquisitionSource> _onAcquisitionChanged;
    private readonly Action<string, bool, HqPropagationMode> _onHqChanged;

    public RecipeTreeUiBuilder(
        Action<string, AcquisitionSource> onAcquisitionChanged,
        Action<string, bool, HqPropagationMode> onHqChanged)
    {
        _onAcquisitionChanged = onAcquisitionChanged;
        _onHqChanged = onHqChanged;
    }

    /// <summary>
    /// Registry mapping NodeId to UI elements for selective updates.
    /// </summary>
    public IReadOnlyDictionary<string, NodeUiElements> NodeUiRegistry => _nodeUiRegistry;

    /// <summary>
    /// Builds the complete recipe tree UI.
    /// </summary>
    public void BuildTree(IEnumerable<PlanNodeViewModel> rootNodes, Panel container)
    {
        _nodeUiRegistry.Clear();
        container.Children.Clear();

        foreach (var rootNode in rootNodes)
        {
            var rootElement = CreateNodeElement(rootNode, depth: 0);
            container.Children.Add(rootElement);
        }
    }

    /// <summary>
    /// Updates just the HQ indicator for a node without rebuilding the tree.
    /// </summary>
    public void UpdateNodeHqIndicator(string nodeId, bool isHq)
    {
        if (_nodeUiRegistry.TryGetValue(nodeId, out var elements))
        {
            if (elements.HqIndicator != null)
            {
                elements.HqIndicator.Text = isHq ? " [HQ]" : "";
                elements.HqIndicator.Foreground = isHq ? Brushes.Gold : Brushes.Transparent;
            }
        }
    }

    /// <summary>
    /// Updates the acquisition source display for a node.
    /// Finds the dropdown item by Tag rather than index to handle dynamic dropdown content.
    /// </summary>
    public void UpdateNodeAcquisition(string nodeId, AcquisitionSource source)
    {
        if (_nodeUiRegistry.TryGetValue(nodeId, out var elements))
        {
            if (elements.Dropdown != null)
            {
                // Find the item with matching Tag instead of using fixed index
                var matchingItem = elements.Dropdown.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag is AcquisitionSource s && s == source);
                
                if (matchingItem != null)
                {
                    elements.Dropdown.SelectedItem = matchingItem;
                }
            }
        }
    }

    private UIElement CreateNodeElement(PlanNodeViewModel nodeVm, int depth)
    {
        if (!nodeVm.Children.Any())
        {
            return CreateLeafNode(nodeVm, depth);
        }
        return CreateParentNode(nodeVm, depth);
    }

    private UIElement CreateParentNode(PlanNodeViewModel nodeVm, int depth)
    {
        Button? collapseMaterialsButton = null;

        var backgroundBrush = depth == 0 
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525"))
            : Brushes.Transparent;

        var expander = new Expander
        {
            IsExpanded = nodeVm.IsExpanded,
            Background = backgroundBrush,
            Padding = new Thickness(depth == 0 ? 8 : 0),
            Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 0),
            Tag = nodeVm
        };

        var headerPanel = CreateNodeHeader(
            nodeVm,
            showDropdown: true,
            showCollapseMaterialsButton: true,
            onCollapseMaterials: () => CollapseDescendantExpanders(expander),
            onCollapseButtonCreated: button => collapseMaterialsButton = button);

        expander.Header = headerPanel;

        expander.Resources["ExpanderHeaderStyle"] = CreateExpanderHeaderStyle();

        // Bind expansion state
        expander.Expanded += (s, e) =>
        {
            nodeVm.IsExpanded = true;
            SetCollapseMaterialsButtonVisibility(collapseMaterialsButton, isExpanded: true);
        };
        expander.Collapsed += (s, e) =>
        {
            nodeVm.IsExpanded = false;
            SetCollapseMaterialsButtonVisibility(collapseMaterialsButton, isExpanded: false);
        };

        SetCollapseMaterialsButtonVisibility(collapseMaterialsButton, expander.IsExpanded);

        // Add children
        var childrenPanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
        foreach (var child in nodeVm.Children.Select(c => new PlanNodeViewModel(c)))
        {
            childrenPanel.Children.Add(CreateNodeElement(child, depth + 1));
        }
        expander.Content = childrenPanel;

        // Register UI elements for updates
        RegisterNodeElements(nodeVm.NodeId, headerPanel);

        return expander;
    }

    private UIElement CreateLeafNode(PlanNodeViewModel nodeVm, int depth)
    {
        var panel = new Grid
        {
            Margin = new Thickness(depth * 16, 2, 0, 2),
            Background = Brushes.Transparent
        };

        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left side: Icon + HQ toggle + Name
        var leftPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Job icon
        var jobIcon = new TextBlock
        {
            Text = GetJobIcon(nodeVm.Job),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        leftPanel.Children.Add(jobIcon);

        // HQ toggle button (if item can be HQ) - now next to the name
        Button? hqButton = null;
        if (nodeVm.CanBeHq)
        {
            hqButton = CreateHqToggleButton(nodeVm);
            leftPanel.Children.Add(hqButton);
        }
        else
        {
            // Add spacer for alignment when no HQ toggle
            leftPanel.Children.Add(new FrameworkElement { Width = 24 });
        }

        // Item name with HQ indicator prefix
        var nameBlock = new TextBlock
        {
            Text = $"{nodeVm.Name} x{nodeVm.Quantity}",
            Foreground = GetNodeForeground(nodeVm),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        leftPanel.Children.Add(nameBlock);

        // HQ indicator text (shows [HQ] when enabled)
        var hqIndicator = new TextBlock
        {
            Name = "HqIndicator",
            Text = nodeVm.MustBeHq ? " [HQ]" : "",
            Foreground = nodeVm.MustBeHq ? Brushes.Gold : Brushes.Transparent,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        leftPanel.Children.Add(hqIndicator);

        // Circular reference indicator
        if (nodeVm.IsCircularReference)
        {
            var circularIndicator = new TextBlock
            {
                Text = " ↻ circular",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "This item is already being crafted higher up in the recipe chain.\nTo avoid infinite loops, purchase this from the market instead."
            };
            leftPanel.Children.Add(circularIndicator);
        }

        Grid.SetColumn(leftPanel, 0);
        panel.Children.Add(leftPanel);

        // Right side: Dropdown only
        var rightPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        // Acquisition source dropdown
        var dropdown = CreateAcquisitionDropdown(nodeVm);
        if (dropdown != null)
        {
            rightPanel.Children.Add(dropdown);
        }

        Grid.SetColumn(rightPanel, 1);
        panel.Children.Add(rightPanel);

        // Register for updates
        RegisterNodeElements(nodeVm.NodeId, panel, dropdown, hqIndicator);

        return panel;
    }

    private Grid CreateNodeHeader(
        PlanNodeViewModel nodeVm,
        bool showDropdown,
        bool showCollapseMaterialsButton = false,
        Action? onCollapseMaterials = null,
        Action<Button>? onCollapseButtonCreated = null)
    {
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Job icon
        var jobIcon = new TextBlock
        {
            Text = GetJobIcon(nodeVm.Job),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 14,
            Width = 20,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        leftPanel.Children.Add(jobIcon);

        // Level indicator
        if (nodeVm.RecipeLevel > 0)
        {
            var levelBlock = new TextBlock
            {
                Text = $"Lv.{nodeVm.RecipeLevel} ",
                Foreground = Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            };
            leftPanel.Children.Add(levelBlock);
        }

        // HQ toggle button (if item can be HQ) - now next to the name
        if (nodeVm.CanBeHq)
        {
            var hqButton = CreateHqToggleButton(nodeVm);
            leftPanel.Children.Add(hqButton);
        }
        else
        {
            // Add spacer for alignment when no HQ toggle
            leftPanel.Children.Add(new FrameworkElement { Width = 24 });
        }

        // Item name with quantity
        var nameBlock = new TextBlock
        {
            Text = $"{nodeVm.Name} x{nodeVm.Quantity}",
            Foreground = GetNodeForeground(nodeVm),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        leftPanel.Children.Add(nameBlock);

        // HQ indicator text (shows [HQ] when enabled)
        var hqIndicator = new TextBlock
        {
            Name = "HqIndicator",
            Text = nodeVm.MustBeHq ? " [HQ]" : "",
            Foreground = nodeVm.MustBeHq ? Brushes.Gold : Brushes.Transparent,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        leftPanel.Children.Add(hqIndicator);

        // Circular reference indicator
        if (nodeVm.IsCircularReference)
        {
            var circularIndicator = new TextBlock
            {
                Text = " ↻ circular",
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800")),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = "This item is already being crafted higher up in the recipe chain.\nTo avoid infinite loops, purchase this from the market instead."
            };
            leftPanel.Children.Add(circularIndicator);
        }

        Grid.SetColumn(leftPanel, 0);
        panel.Children.Add(leftPanel);

        var rightPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (showCollapseMaterialsButton && onCollapseMaterials != null)
        {
            var collapseMaterialsButton = new Button
            {
                Content = "Collapse Materials",
                Height = 22,
                FontSize = 10,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                Foreground = Brushes.LightGray,
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Collapse all child material nodes",
                Visibility = nodeVm.IsExpanded ? Visibility.Visible : Visibility.Collapsed
            };

            collapseMaterialsButton.Click += (s, e) =>
            {
                onCollapseMaterials();
                e.Handled = true;
            };

            onCollapseButtonCreated?.Invoke(collapseMaterialsButton);

            rightPanel.Children.Add(collapseMaterialsButton);
        }

        // Right-side controls
        if (showDropdown)
        {
            // Acquisition dropdown
            var dropdown = CreateAcquisitionDropdown(nodeVm);
            if (dropdown != null)
            {
                rightPanel.Children.Add(dropdown);
            }
        }

        Grid.SetColumn(rightPanel, 1);
        panel.Children.Add(rightPanel);

        return panel;
    }

    private static void CollapseDescendantExpanders(Expander parentExpander)
    {
        if (parentExpander.Content is not Panel panel)
        {
            return;
        }

        foreach (var childExpander in panel.Children.OfType<Expander>())
        {
            childExpander.IsExpanded = false;
            CollapseDescendantExpanders(childExpander);
        }
    }

    private static void SetCollapseMaterialsButtonVisibility(Button? button, bool isExpanded)
    {
        if (button == null)
        {
            return;
        }

        button.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private ComboBox? CreateAcquisitionDropdown(PlanNodeViewModel nodeVm)
    {
        var dropdown = new ComboBox
        {
            Width = 120,
            Height = 22,
            FontSize = 10,
            Padding = new Thickness(2, 0, 0, 0),
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#555555")),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };

        // Track all items by their source for selection
        var sourceToItem = new Dictionary<AcquisitionSource, List<ComboBoxItem>>();

        // Add Craft option if craftable
        if (nodeVm.CanCraft)
        {
            var craftItem = new ComboBoxItem { Content = "Craft", Tag = AcquisitionSource.Craft };
            dropdown.Items.Add(craftItem);
            sourceToItem[AcquisitionSource.Craft] = new List<ComboBoxItem> { craftItem };
        }

        // Add Market Buy options
        var buyNqItem = new ComboBoxItem { Content = "Buy NQ", Tag = AcquisitionSource.MarketBuyNq };
        dropdown.Items.Add(buyNqItem);
        sourceToItem[AcquisitionSource.MarketBuyNq] = new List<ComboBoxItem> { buyNqItem };

        if (nodeVm.CanBeHq)
        {
            var buyHqItem = new ComboBoxItem { Content = "Buy HQ", Tag = AcquisitionSource.MarketBuyHq };
            dropdown.Items.Add(buyHqItem);
            sourceToItem[AcquisitionSource.MarketBuyHq] = new List<ComboBoxItem> { buyHqItem };
        }

        // Add Vendor options - one per cheapest gil vendor
        if (nodeVm.CanBuyFromVendor && nodeVm.VendorOptions?.Any() == true)
        {
            var gilVendors = nodeVm.VendorOptions.Where(v => v.IsGilVendor).ToList();
            if (gilVendors.Any())
            {
                var minPrice = gilVendors.Min(v => v.Price);
                var cheapestVendors = gilVendors.Where(v => v.Price == minPrice).ToList();

                var vendorItems = new List<ComboBoxItem>();
                foreach (var vendor in cheapestVendors)
                {
                    var vendorItem = new ComboBoxItem
                    {
                        Content = $"Vendor: {vendor.DisplayName}",
                        Tag = AcquisitionSource.VendorBuy,
                        ToolTip = $"{vendor.FullDisplayText}"
                    };
                    dropdown.Items.Add(vendorItem);
                    vendorItems.Add(vendorItem);
                }
                sourceToItem[AcquisitionSource.VendorBuy] = vendorItems;
            }
        }

        // Set selected item based on current source
        ComboBoxItem? selectedItem = null;
        if (sourceToItem.TryGetValue(nodeVm.Source, out var items) && items.Any())
        {
            // For vendors, use selected index if valid
            if (nodeVm.Source == AcquisitionSource.VendorBuy && nodeVm.SelectedVendorIndex >= 0 && nodeVm.SelectedVendorIndex < items.Count)
            {
                selectedItem = items[nodeVm.SelectedVendorIndex];
            }
            else
            {
                selectedItem = items.First();
            }
        }
        dropdown.SelectedItem = selectedItem ?? sourceToItem.GetValueOrDefault(AcquisitionSource.MarketBuyNq)?.First() ?? dropdown.Items[0];

        dropdown.SelectionChanged += (s, e) =>
        {
            if (dropdown.SelectedItem is ComboBoxItem item && item.Tag is AcquisitionSource newSource)
            {
                // For vendor selection, track which vendor was selected
                int vendorIndex = -1;
                if (newSource == AcquisitionSource.VendorBuy && sourceToItem.TryGetValue(AcquisitionSource.VendorBuy, out var vendorItems))
                {
                    vendorIndex = vendorItems.IndexOf(item);
                }
                _onAcquisitionChanged(nodeVm.NodeId, newSource);
                // TODO: Pass vendorIndex to callback for procurement planning
            }
        };

        return dropdown;
    }

    private Button CreateHqToggleButton(PlanNodeViewModel nodeVm)
    {
        var button = new Button
        {
            Content = "★",
            Width = 24,
            Height = 22,
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Toggle HQ requirement"
        };

        // Style based on current HQ state
        UpdateHqButtonStyle(button, nodeVm.MustBeHq);

        button.Click += (s, e) =>
        {
            // Cycle through: None -> LeafChildren -> AllChildren -> None
            var mode = HqPropagationMode.None; // Simplified - just toggle for now
            _onHqChanged(nodeVm.NodeId, !nodeVm.MustBeHq, mode);
        };

        return button;
    }

    private void RegisterNodeElements(string nodeId, Panel panel, ComboBox? dropdown = null, TextBlock? hqIndicator = null)
    {
        _nodeUiRegistry[nodeId] = new NodeUiElements
        {
            Panel = panel,
            Dropdown = dropdown,
            HqIndicator = hqIndicator ?? panel.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == "HqIndicator")
        };
    }

    private static void UpdateHqButtonStyle(Button button, bool isHq)
    {
        button.Foreground = isHq ? Brushes.Gold : Brushes.Gray;
        button.Background = isHq 
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3d3d3d"))
            : Brushes.Transparent;
    }

    private static Brush GetNodeForeground(PlanNodeViewModel node)
    {
        var colorName = RecipePlanDisplayHelpers.GetSourceColorName(node.Source);
        return colorName switch
        {
            "LightBlue" => Brushes.LightSkyBlue,
            "LightGreen" => Brushes.LightGreen,
            _ => Brushes.White
        };
    }

    private static string GetJobIcon(string job)
    {
        return RecipePlanDisplayHelpers.GetJobIcon(job);
    }

    private static int GetDropdownIndexForSource(AcquisitionSource source)
    {
        return source switch
        {
            AcquisitionSource.Craft => 0,
            AcquisitionSource.MarketBuyNq => 1,
            AcquisitionSource.MarketBuyHq => 2,
            _ => 0
        };
    }

    private static AcquisitionSource GetSourceFromDropdownIndex(int index, bool canBeHq)
    {
        return index switch
        {
            0 => AcquisitionSource.Craft,
            1 => AcquisitionSource.MarketBuyNq,
            2 when canBeHq => AcquisitionSource.MarketBuyHq,
            _ => AcquisitionSource.Craft
        };
    }

    private static Style CreateExpanderHeaderStyle()
    {
        var style = new Style(typeof(Expander));
        
        // Override the ToggleButton template to hide the default arrow
        var toggleButtonStyle = new Style(typeof(ToggleButton));
        toggleButtonStyle.Setters.Add(new Setter(Control.TemplateProperty, CreateEmptyToggleButtonTemplate()));
        
        style.Resources.Add(typeof(ToggleButton), toggleButtonStyle);
        return style;
    }
    
    private static ControlTemplate CreateEmptyToggleButtonTemplate()
    {
        // Create a template that just shows the content, no arrow
        var template = new ControlTemplate(typeof(ToggleButton));
        var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        contentPresenter.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Content") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        contentPresenter.SetBinding(ContentPresenter.ContentTemplateProperty, new System.Windows.Data.Binding("ContentTemplate") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        template.VisualTree = contentPresenter;
        return template;
    }
}

/// <summary>
/// Holds references to UI elements for a node for selective updates.
/// </summary>
public class NodeUiElements
{
    public Panel Panel { get; set; } = null!;
    public ComboBox? Dropdown { get; set; }
    public TextBlock? HqIndicator { get; set; }
}
