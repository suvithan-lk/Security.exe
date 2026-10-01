namespace Security.Core.Entities;

/// <summary>
/// Simple key/value application setting persisted in the local database.
/// Configuration (appsettings.json) supplies defaults; the database stores overrides.
/// </summary>
public class ApplicationSetting
{
    public int Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
