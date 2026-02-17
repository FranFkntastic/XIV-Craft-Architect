using System.Windows;
using System.Windows.Controls;

namespace FFXIV_Craft_Architect;

/// <summary>
/// Dialog for selecting an item from a list.
/// </summary>
public partial class ItemSelectionDialog : Window
{
    public int SelectedIndex { get; private set; } = -1;

    public ItemSelectionDialog(List<string> items, string title)
    {
        InitializeComponent();
        
        DialogTitle.Text = title;
        ItemsList.ItemsSource = items;
        
        if (items.Count > 0)
        {
            ItemsList.SelectedIndex = 0;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectButton.IsEnabled = ItemsList.SelectedIndex >= 0;
    }

    private void OnDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsList.SelectedIndex >= 0)
        {
            SelectedIndex = ItemsList.SelectedIndex;
            DialogResult = true;
            Close();
        }
    }

    private void OnSelect(object sender, RoutedEventArgs e)
    {
        SelectedIndex = ItemsList.SelectedIndex;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
