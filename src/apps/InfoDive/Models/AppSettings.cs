using System.Text.Json.Serialization;

namespace InfoDive.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PresentationScaleMode
{
    FitWidth,
    FitHeight,
}

/// <summary>
/// App-wide settings persisted in %APPDATA%/InfoDive/settings.json.
/// Use for truly global preferences (not project-specific). Project-level
/// settings belong in <see cref="ProjectSettings"/>.
/// </summary>
public class AppSettings
{
    // Placeholder for future app-level settings.
    // e.g. recent files list, theme, default export path, etc.
}
