using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FFXIVCraftArchitect.Core.Models;
using FFXIVCraftArchitect.ViewModels;

namespace FFXIVCraftArchitect.UIBuilders;

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
            elements.HqIndicator.Text = isHq ? " [HQ]" : "";
            elements.HqIndicator.Foreground = isHq ? Brushes.Gold : Brushes.Transparent;
        }
    }

    /// <summary>
    /// Updates the acquisition source display for a node.
    /// </summary>
    public void UpdateNodeAcquisition(string nodeId, AcquisitionSource source)
    {
        if (_nodeUiRegistry.TryGetValue(nodeId, out var elements))
        {
            if (elements.Dropdown != null)
            {
                elements.Dropdown.SelectedIndex = GetDropdownIndexForSource(source);
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
        var headerPanel = CreateNodeHeader(nodeVm, showDropdown: true);
        
        var backgroundBrush = depth == 0 
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252525"))
            : Brushes.Transparent;

        var expander = new Expander
        {
            Header = headerPanel,
            IsExpanded = nodeVm.IsExpanded,
            Background = backgroundBrush,
            Padding = new Thickness(depth == 0 ? 8 : 0),
            Margin = new Thickness(0, 0, 0, depth == 0 ? 8 : 0),
            Tag = nodeVm
        };

        expander.Resources["ExpanderHeaderStyle"] = CreateExpanderHeaderStyle();

        // Bind expansion state
        expander.Expanded += (s, e) => nodeVm.IsExpanded = true;
        expander.Collapsed += (s, e) => nodeVm.IsExpanded = false;

        // Add children
        var childrenPanel = new StackPanel { Margin = new Thickness(16, 4, 0, 0) };
        foreach (var child in nodeVm.Children.Select(c => new PlanNodeViewModel(c)))
        {
            childrenPanel.Children.Add(CreateNodeElement(child, depth + 1));
        }
        expander.Content = childrenPanel;

        // Add expand/collapse buttons for root nodes
        if (depth == 0 && headerPanel is StackPanel hp)
        {
            AddRootExpandCollapseButtons(hp, expander);
        }

        // Register UI elements for updates
        RegisterNodeElements(nodeVm.NodeId, headerPanel);

        return expander;
    }

    private UIElement CreateLeafNode(PlanNodeViewModel nodeVm, int depth)
    {
        var panel = new DockPanel
        {
            Margin = new Thickness(depth * 16, 2, 0, 2),
            Background = Brushes.Transparent
        };

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

        DockPanel.SetDock(leftPanel, Dock.Left);
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

        DockPanel.SetDock(rightPanel, Dock.Right);
        panel.Children.Add(rightPanel);

        // Register for updates
        RegisterNodeElements(nodeVm.NodeId, panel, dropdown, hqIndicator);

        return panel;
    }

    private StackPanel CreateNodeHeader(PlanNodeViewModel nodeVm, bool showDropdown)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

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
        panel.Children.Add(jobIcon);

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
            panel.Children.Add(levelBlock);
        }

        // HQ toggle button (if item can be HQ) - now next to the name
        if (nodeVm.CanBeHq)
        {
            var hqButton = CreateHqToggleButton(nodeVm);
            panel.Children.Add(hqButton);
        }
        else
        {
            // Add spacer for alignment when no HQ toggle
            panel.Children.Add(new FrameworkElement { Width = 24 });
        }

        // Item name with quantity
        var nameBlock = new TextBlock
        {
            Text = $"{nodeVm.Name} x{nodeVm.Quantity}",
            Foreground = GetNodeForeground(nodeVm),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        panel.Children.Add(nameBlock);

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
        panel.Children.Add(hqIndicator);

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
            panel.Children.Add(circularIndicator);
        }

        // Spacer
        panel.Children.Add(new FrameworkElement { Width = 8 });

        // Right-side controls: Dropdown only
        if (showDropdown)
        {
            // Acquisition dropdown
            var dropdown = CreateAcquisitionDropdown(nodeVm);
            if (dropdown != null)
            {
                panel.Children.Add(dropdown);
            }
        }

        return panel;
    }

    private ComboBox? CreateAcquisitionDropdown(PlanNodeViewModel nodeVm)
    {
        // Skip dropdown for items that can't be traded
        if (nodeVm.Source == AcquisitionSource.VendorBuy)
        {
            return null;
        }

        var dropdown = new ComboBox
        {
            Width = 100,
            Height = 22,
            FontSize = 11,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        dropdown.Items.Add("Craft");
        dropdown.Items.Add("Buy NQ");
        if (nodeVm.CanBeHq)
        {
            dropdown.Items.Add("Buy HQ");
        }

        dropdown.SelectedIndex = GetDropdownIndexForSource(nodeVm.Source);

        dropdown.SelectionChanged += (s, e) =>
        {
            if (dropdown.SelectedIndex >= 0)
            {
                var newSource = GetSourceFromDropdownIndex(dropdown.SelectedIndex, nodeVm.CanBeHq);
                _onAcquisitionChanged(nodeVm.NodeId, newSource);
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

    private void AddRootExpandCollapseButtons(StackPanel headerPanel, Expander rootExpander)
    {
        // Expand button
        var expandButton = new TextBlock
        {
            Text = "+",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Expand all subcrafts"
        };
        expandButton.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            SetExpanderSubtreeState(rootExpander, true);
        };

        // Collapse button
        var collapseButton = new TextBlock
        {
            Text = "−",
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "Collapse all subcrafts"
        };
        collapseButton.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            SetExpanderSubtreeState(rootExpander, false);
        };

        headerPanel.Children.Add(expandButton);
        headerPanel.Children.Add(collapseButton);
    }

    private void SetExpanderSubtreeState(Expander expander, bool isExpanded)
    {
        expander.IsExpanded = isExpanded;
        
        if (expander.Content is StackPanel childrenPanel)
        {
            foreach (var child in childrenPanel.Children.OfType<Expander>())
            {
                SetExpanderSubtreeState(child, isExpanded);
            }
        }
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
        return node.Source switch
        {
            AcquisitionSource.MarketBuyNq or AcquisitionSource.MarketBuyHq => Brushes.LightSkyBlue,
            AcquisitionSource.VendorBuy => Brushes.LightGreen,
            _ => Brushes.White
        };
    }

    private static string GetJobIcon(string job)
    {
        return job switch
        {
            "Carpenter" => "🪚",
            "Blacksmith" => "⚒️",
            "Armorer" => "🛡️",
            "Goldsmith" => "💍",
            "Leatherworker" => "🧵",
            "Weaver" => "🧶",
            "Alchemist" => "⚗️",
            "Culinarian" => "🍳",
            "Company Workshop" => "🏢",
            "Phase" => "📋",
            _ => "•"
        };
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
        // Simplified - in real implementation, would set custom template
        return style;
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
