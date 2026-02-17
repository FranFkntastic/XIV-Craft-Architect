using System.Windows.Controls;
using FFXIV_Craft_Architect.ViewModels;

namespace FFXIV_Craft_Architect.Services.Interfaces;

/// <summary>
/// Types of info cards with associated color schemes.
/// </summary>
public enum CardType
{
    /// <summary>Vendor items - olive green (#3e4a2d)</summary>
    Vendor,
    /// <summary>Market items - blue-gray (#2d3d4a)</summary>
    Market,
    /// <summary>Untradeable items - brown (#4a3d2d)</summary>
    Untradeable,
    /// <summary>Loading state - yellow-gray (#3d3e2d)</summary>
    Loading,
    /// <summary>Cached data - yellow-gray (#3d3e2d)</summary>
    Cached,
    /// <summary>Error state - red (#4a2d2d)</summary>
    Error,
    /// <summary>Neutral state - gray (#2d2d2d)</summary>
    Neutral
}

/// <summary>
/// Visual styles for data-bound market cards.
/// </summary>
public enum CardStyle
{
    /// <summary>Legacy expandable card with full width and accent background.</summary>
    Legacy,
    /// <summary>Compact collapsed card with fixed width and neutral background.</summary>
    Collapsed
}

/// <summary>
/// Factory for creating standardized card UI elements.
/// Centralizes card creation logic for consistent styling across the application.
/// </summary>
public interface ICardFactory
{
    /// <summary>
    /// Creates an info card with a title and optional content text.
    /// </summary>
    /// <param name="title">The card title.</param>
    /// <param name="content">Optional content text. If null or whitespace, displays "(No content)".</param>
    /// <param name="type">The card type determining the background color.</param>
    /// <returns>A configured Border element representing the info card.</returns>
    Border CreateInfoCard(string title, string? content, CardType type);

    /// <summary>
    /// Creates an info card with a title and a list of items.
    /// </summary>
    /// <param name="title">The card title.</param>
    /// <param name="items">Collection of items to display as bullet points. If empty, displays "(No items)".</param>
    /// <param name="type">The card type determining the background color.</param>
    /// <returns>A configured Border element representing the info card.</returns>
    Border CreateInfoCard(string title, IEnumerable<string> items, CardType type);

    /// <summary>
    /// Creates a data-bound card for market data display.
    /// </summary>
    /// <param name="viewModel">The MarketCardViewModel to bind to the card.</param>
    /// <param name="style">The visual style for the card (Legacy or Collapsed).</param>
    /// <returns>A configured Border element containing a data-bound ContentControl.</returns>
    /// <exception cref="ArgumentNullException">Thrown when viewModel is null.</exception>
    Border CreateDataBoundCard(MarketCardViewModel viewModel, CardStyle style);

    /// <summary>
    /// Creates a placeholder card for empty/initial states.
    /// </summary>
    /// <param name="title">The card title.</param>
    /// <param name="message">The placeholder message.</param>
    /// <returns>A configured Border element representing the placeholder card.</returns>
    Border CreatePlaceholder(string title, string message);

    /// <summary>
    /// Creates an error card for displaying error messages.
    /// </summary>
    /// <param name="title">The card title.</param>
    /// <param name="errorMessage">The error message to display.</param>
    /// <returns>A configured Border element representing the error card.</returns>
    Border CreateErrorCard(string title, string errorMessage);

    /// <summary>
    /// Creates a placeholder panel for when market data is not available.
    /// </summary>
    /// <param name="materialCount">The number of materials that need analysis.</param>
    /// <param name="actionPrompt">The prompt text telling the user what action to take.</param>
    /// <returns>A configured StackPanel containing the placeholder content.</returns>
    Panel CreateMarketPlaceholderPanel(int materialCount, string actionPrompt);

    /// <summary>
    /// Creates a clickable collapsed market card for split-pane view.
    /// </summary>
    /// <param name="viewModel">The view model containing market data.</param>
    /// <param name="isExpanded">Whether this card is currently expanded.</param>
    /// <param name="onClick">Action to invoke when card is clicked.</param>
    /// <param name="findResource">Optional custom resource lookup function.</param>
    /// <returns>A configured Border element with click handler attached.</returns>
    Border CreateCollapsedMarketCard(MarketCardViewModel viewModel, bool isExpanded, Action onClick, Func<string, object>? findResource = null);
}
