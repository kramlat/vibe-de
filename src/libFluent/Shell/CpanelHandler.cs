public interface IConfigPanel : IDBusInterface
{
    // Unique ID for routing and search
    string PanelId { get; }

    // Display name (localized)
    string Title { get; }

    // Category (System, Network, Personalization, etc.)
    string Category { get; }

    // Icon (symbolic name or path)
    string Icon { get; }

    // The Avalonia control that renders the panel
    Task<object> CreateViewAsync();

    // Optional: search keywords
    Task<string[]> GetKeywordsAsync();

    // Optional: panel metadata (version, provider, etc.)
    Task<Dictionary<string,string>> GetMetadataAsync();
}
