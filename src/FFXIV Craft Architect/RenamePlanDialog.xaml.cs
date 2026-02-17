using System.Windows;

namespace FFXIV_Craft_Architect;

/// <summary>
/// Simple dialog for renaming a plan.
/// </summary>
public partial class RenamePlanDialog : Window
{
    public string NewName { get; set; }

    public RenamePlanDialog(string currentName)
    {
        InitializeComponent();
        NewName = currentName;
        DataContext = this;
        
        // Select all text when dialog opens
        Loaded += (s, e) => NameTextBox.SelectAll();
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NewName))
        {
            DialogResult = true;
            Close();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
