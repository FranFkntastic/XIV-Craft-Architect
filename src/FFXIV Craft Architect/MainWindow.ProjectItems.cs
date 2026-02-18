using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FFXIV_Craft_Architect;

public partial class MainWindow
{
    /// <summary>
    /// Validates quantity input to allow only digits.
    /// </summary>
    private void OnQuantityPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsDigit);
    }

    /// <summary>
    /// Selects all text when quantity field gets focus.
    /// </summary>
    private void OnQuantityGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    /// <summary>
    /// Handles quantity change in project items list.
    /// </summary>
    private void OnQuantityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            var item = textBox.FindParent<ListBoxItem>();
            if (item?.DataContext is ProjectItem projectItem)
            {
                if (!int.TryParse(textBox.Text, out var quantity) || quantity < 1)
                {
                    quantity = 1;
                    textBox.Text = "1";
                }

                projectItem.Quantity = quantity;
                StatusLabel.Text = $"Updated {projectItem.Name} quantity to {quantity}";

                ProjectList.Items.Refresh();
            }
        }
    }

    /// <summary>
    /// Removes an item from the project list.
    /// </summary>
    private void OnRemoveProjectItem(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            var listBoxItem = button.FindParent<ListBoxItem>();
            if (listBoxItem?.DataContext is ProjectItem projectItem)
            {
                _recipeVm.RemoveProjectItemCommand.Execute(projectItem.Id);

                StatusLabel.Text = $"Removed {projectItem.Name} from project";

                BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
                BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;

                ProjectList.ItemsSource = null;
                ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
                UpdateQuickViewCount();
            }
        }
    }

    /// <summary>
    /// Updates the quick view count display in the project panel.
    /// </summary>
    private void UpdateQuickViewCount()
    {
        if (_recipeVm.ProjectItems.Count <= 5)
        {
            QuickViewCountText.Text = $"({_recipeVm.ProjectItems.Count})";
        }
        else
        {
            QuickViewCountText.Text = $"(showing 5 of {_recipeVm.ProjectItems.Count})";
        }
    }

    /// <summary>
    /// Opens the Project Items management window.
    /// </summary>
    private void OnManageItemsClick(object sender, RoutedEventArgs e)
    {
        var planName = _currentPlan?.Name;
        var logger = App.Services.GetRequiredService<ILogger<Views.ProjectItemsWindow>>();

        var window = new Views.ProjectItemsWindow(
            _recipeVm.ProjectItems.ToList(),
            planName,
            onItemsChanged: (items) =>
            {
                BuildPlanButton.IsEnabled = items.Count > 0;
                BrowsePlanButton.IsEnabled = items.Count > 0;
            },
            onAddItem: null,
            logger: logger)
        {
            Owner = this
        };

        window.ShowDialog();

        ProjectList.ItemsSource = null;
        ProjectList.ItemsSource = _recipeVm.ProjectItems.ToList();
        UpdateQuickViewCount();
        BuildPlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;
        BrowsePlanButton.IsEnabled = _recipeVm.ProjectItems.Count > 0;

        StatusLabel.Text = $"Project items updated: {_recipeVm.ProjectItems.Count} items";
    }
}
