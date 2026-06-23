using System.Text.Json.Serialization;

namespace InfoDive.Models;

/// <summary>
/// A single image entry in a project. Projects may contain multiple images
/// (each keyframe references one via <see cref="Keyframe.ImageId"/>).
/// </summary>
public class ImageEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public class ManifestData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0";

    /// <summary>Legacy single-image field (v1.0). Still written for backward compat readers.</summary>
    [JsonPropertyName("imageFileName")]
    public string ImageFileName { get; set; } = "";

    /// <summary>Multi-image list (v2.0+). If populated, supersedes <see cref="ImageFileName"/>.</summary>
    [JsonPropertyName("images")]
    public List<ImageEntry> Images { get; set; } = [];
}

public class CanvasInfo
{
    [JsonPropertyName("imageWidth")]
    public int ImageWidth { get; set; }

    [JsonPropertyName("imageHeight")]
    public int ImageHeight { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LaunchMode
{
    Editor,
    Presentation,
}

/// <summary>
/// Project-level settings persisted inside the .przip file.
/// Add new fields here as needed — each must be nullable or have a safe default
/// so older files still load correctly.
/// </summary>
public class ProjectSettings
{
    [JsonPropertyName("presentationScaleMode")]
    public PresentationScaleMode PresentationScaleMode { get; set; } = PresentationScaleMode.FitWidth;

    /// <summary>
    /// Mode used when the file is opened without an explicit -e/-p flag.
    /// </summary>
    [JsonPropertyName("defaultLaunchMode")]
    public LaunchMode DefaultLaunchMode { get; set; } = LaunchMode.Editor;
}

public class ScenarioData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "2.0";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("canvas")]
    public CanvasInfo Canvas { get; set; } = new();

    [JsonPropertyName("settings")]
    public ProjectSettings Settings { get; set; } = new();

    [JsonPropertyName("keyframes")]
    public List<Keyframe> Keyframes { get; set; } = [];
}
