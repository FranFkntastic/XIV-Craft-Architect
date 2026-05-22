namespace FFXIV_Craft_Architect.ViewModels;

public enum MarketInfoCardKind
{
    Neutral,
    Vendor,
    Cached,
    Untradeable,
    Error
}

public class MarketInfoCardViewModel : ViewModelBase
{
    public MarketInfoCardViewModel(string title, string content, MarketInfoCardKind kind)
    {
        Title = title;
        Content = content;
        Kind = kind;
    }

    public string Title { get; }

    public string Content { get; }

    public MarketInfoCardKind Kind { get; }
}
