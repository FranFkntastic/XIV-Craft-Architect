using System.Text.Json.Serialization;

namespace FFXIVCraftArchitect.Core.Models;

/// <summary>
/// Represents a vendor that sells an item.
/// Stores full vendor details including name, location, price, and currency.
/// </summary>
public class VendorInfo
{
    /// <summary>
    /// Vendor name (e.g., "Material Supplier", "Maisenta")
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Vendor location (e.g., "Limsa Lominsa", "Gridania")
    /// </summary>
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;
    
    /// <summary>
    /// Price in the specified currency
    /// </summary>
    [JsonPropertyName("price")]
    public decimal Price { get; set; }
    
    /// <summary>
    /// Currency type (usually "gil" but can be tomestones, etc.)
    /// </summary>
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "gil";
    
    /// <summary>
    /// Whether this vendor accepts gil (standard currency)
    /// </summary>
    [JsonIgnore]
    public bool IsGilVendor => Currency?.ToLowerInvariant() == "gil";
    
    /// <summary>
    /// Display text for the vendor (Name + Location)
    /// </summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(Location) 
        ? Name 
        : $"{Name} ({Location})";
    
    /// <summary>
    /// Full display text including price
    /// </summary>
    [JsonIgnore]
    public string FullDisplayText => IsGilVendor
        ? $"{DisplayName} - {Price:N0}g"
        : $"{DisplayName} - {Price:N0} {Currency}";

    /// <summary>
    /// Alternate locations where this vendor can be found.
    /// For vendors like "Material Supplier" that appear in multiple housing districts.
    /// </summary>
    [JsonIgnore]
    public List<string> AlternateLocations { get; set; } = new();

    /// <summary>
    /// Whether this vendor has multiple locations available.
    /// </summary>
    [JsonIgnore]
    public bool HasMultipleLocations => AlternateLocations.Count > 0;

    /// <summary>
    /// Creates a VendorInfo from a GarlandVendor
    /// </summary>
    public static VendorInfo FromGarlandVendor(GarlandVendor vendor)
    {
        return new VendorInfo
        {
            Name = vendor.Name,
            Location = vendor.Location,
            Price = vendor.Price,
            Currency = vendor.Currency?.ToLowerInvariant() ?? "gil"
        };
    }
}
