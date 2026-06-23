using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace InfoDive.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageTransition
{
    /// <summary>Hard cut (no transition).</summary>
    None,
    /// <summary>Fade out the previous image, then fade in the new one (through black).</summary>
    Fade,
    /// <summary>Cross-fade: simultaneously fade out old and in new.</summary>
    CrossFade,
}

public class Keyframe : INotifyPropertyChanged
{
    private string _id = "";
    private string _name = "";
    private string _imageId = "";
    private double _tx;
    private double _ty;
    private double _scale = 1.0;
    private int _durationMs = 1200;
    private string _easing = "ease-in-out";
    private int _dwellTimeMs;
    private double _refCanvasW;
    private double _refCanvasH;
    private ImageTransition _imageTransition = ImageTransition.Fade;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    /// <summary>
    /// ID of the image this keyframe references. Empty string means the first (default) image.
    /// Used for future multi-image support.
    /// </summary>
    [JsonPropertyName("imageId")]
    public string ImageId
    {
        get => _imageId;
        set => SetField(ref _imageId, value);
    }

    [JsonPropertyName("tx")]
    public double Tx
    {
        get => _tx;
        set => SetField(ref _tx, value);
    }

    [JsonPropertyName("ty")]
    public double Ty
    {
        get => _ty;
        set => SetField(ref _ty, value);
    }

    [JsonPropertyName("sc")]
    public double Scale
    {
        get => _scale;
        set => SetField(ref _scale, value);
    }

    [JsonPropertyName("duration")]
    public int DurationMs
    {
        get => _durationMs;
        set => SetField(ref _durationMs, value);
    }

    [JsonPropertyName("easing")]
    public string Easing
    {
        get => _easing;
        set => SetField(ref _easing, value);
    }

    [JsonPropertyName("dwellTime")]
    public int DwellTimeMs
    {
        get => _dwellTimeMs;
        set => SetField(ref _dwellTimeMs, value);
    }

    /// <summary>Editor canvas width when this keyframe was captured. 0 = unknown (legacy).</summary>
    [JsonPropertyName("refCanvasW")]
    public double RefCanvasW
    {
        get => _refCanvasW;
        set => SetField(ref _refCanvasW, value);
    }

    /// <summary>Editor canvas height when this keyframe was captured. 0 = unknown (legacy).</summary>
    [JsonPropertyName("refCanvasH")]
    public double RefCanvasH
    {
        get => _refCanvasH;
        set => SetField(ref _refCanvasH, value);
    }

    /// <summary>
    /// Visual transition used when the image changes from the previous keyframe.
    /// Ignored when the previous keyframe uses the same image (smooth pan/zoom is used instead).
    /// </summary>
    [JsonPropertyName("imageTransition")]
    public ImageTransition ImageTransition
    {
        get => _imageTransition;
        set => SetField(ref _imageTransition, value);
    }

    /// <summary>
    /// Transient flag (not serialized): true when this keyframe belongs to the currently
    /// displayed image. Used by the UI to highlight matching keyframes and dim others.
    /// </summary>
    private bool _isForCurrentImage = true;
    [JsonIgnore]
    public bool IsForCurrentImage
    {
        get => _isForCurrentImage;
        set => SetField(ref _isForCurrentImage, value);
    }

    /// <summary>
    /// Transient flag (not serialized): true when the keyframe card shows its full detail panel
    /// (duration slider, transition, action icons). Collapsed by default to reduce list density.
    /// </summary>
    private bool _isExpanded;
    [JsonIgnore]
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>
    /// Transient flag (not serialized): true when this is the currently selected keyframe
    /// in the sidebar. Used to show a distinctive visual state and to target keyboard shortcuts.
    /// </summary>
    private bool _isSelected;
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    [JsonIgnore]
    public double DurationSeconds
    {
        get => DurationMs / 1000.0;
        set => DurationMs = (int)(value * 1000);
    }

    public Keyframe Clone()
    {
        return new Keyframe
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = Name + " (コピー)",
            ImageId = ImageId,
            Tx = Tx,
            Ty = Ty,
            Scale = Scale,
            DurationMs = DurationMs,
            Easing = Easing,
            DwellTimeMs = DwellTimeMs,
            RefCanvasW = RefCanvasW,
            RefCanvasH = RefCanvasH,
            ImageTransition = ImageTransition,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
