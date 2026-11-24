namespace FFXIV_Craft_Architect.Web.Dialogs;

/// <summary>What the Duplicate ×N dialog asked for: copy count, base title, and whether to price.</summary>
public sealed record BatchDuplicateOrderRequest(int Count, string? BaseTitle, bool Price);
