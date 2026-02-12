namespace FFXIVCraftArchitect.Core.Services.Interfaces;

/// <summary>
/// Service for managing application settings.
/// Abstracts platform-specific storage (file system, IndexedDB, etc.)
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Get a setting value by dot-separated path.
    /// </summary>
    T? Get<T>(string keyPath, T? defaultValue = default);
    
    /// <summary>
    /// Set a setting value by dot-separated path.
    /// </summary>
    void Set<T>(string keyPath, T value);
    
    /// <summary>
    /// Get a setting value asynchronously (for platforms like Web that need async storage).
    /// </summary>
    Task<T?> GetAsync<T>(string keyPath, T? defaultValue = default);
    
    /// <summary>
    /// Set a setting value asynchronously (for platforms like Web that need async storage).
    /// </summary>
    Task SetAsync<T>(string keyPath, T value);
    
    /// <summary>
    /// Reset all settings to defaults.
    /// </summary>
    Task ResetToDefaultsAsync();
}
