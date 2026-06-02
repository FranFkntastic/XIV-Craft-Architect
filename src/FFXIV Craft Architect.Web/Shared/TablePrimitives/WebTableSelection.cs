namespace FFXIV_Craft_Architect.Web.Shared.TablePrimitives;

public readonly record struct WebTableSelection<TRowKey>
    where TRowKey : notnull
{
    private WebTableSelection(TRowKey? selectedKey, bool hasSelection)
    {
        SelectedKey = selectedKey;
        HasSelection = hasSelection;
    }

    public static WebTableSelection<TRowKey> None { get; } = new(default, false);

    public TRowKey? SelectedKey { get; }

    public bool HasSelection { get; }

    public static WebTableSelection<TRowKey> Single(TRowKey key)
    {
        return new WebTableSelection<TRowKey>(key, true);
    }

    public bool IsSelected(TRowKey key)
    {
        return HasSelection &&
            SelectedKey is not null &&
            EqualityComparer<TRowKey>.Default.Equals(SelectedKey, key);
    }
}
