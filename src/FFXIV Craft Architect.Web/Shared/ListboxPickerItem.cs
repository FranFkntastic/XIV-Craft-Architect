namespace FFXIV_Craft_Architect.Web.Shared;

/// <summary>
/// One choice in a <see cref="ListboxPicker"/>: a label with an optional one-line detail and a
/// right-aligned amount. A disabled item stays readable and focusable but cannot be chosen;
/// <see cref="IsWarning"/> styles its detail as a caution.
/// </summary>
public sealed record ListboxPickerItem(
    string Value,
    string Label,
    string? Detail = null,
    string? Amount = null,
    bool Disabled = false,
    bool IsWarning = false);
