using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace InfoDive.Models;

/// <summary>
/// An image loaded in memory. One project may hold several of these; keyframes
/// reference a specific one via <see cref="Keyframe.ImageId"/>.
/// </summary>
public class LoadedImage : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public byte[] Bytes { get; init; } = [];
    public required BitmapImage Bitmap { get; init; }

    /// <summary>Display name for the image list (falls back to filename).</summary>
    public string DisplayName => FileName;

    /// <summary>
    /// Transient count of keyframes referencing this image. Kept in sync by the owner
    /// (MainWindow) so the image picker can show "filename (N シーン)" without recomputing.
    /// </summary>
    private int _sceneCount;
    public int SceneCount
    {
        get => _sceneCount;
        set
        {
            if (_sceneCount == value) return;
            _sceneCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// True when this image has at least one ink stroke. Driven by MainWindow when the
    /// per-image StrokeCollection changes, so the image list can show a pencil badge.
    /// </summary>
    private bool _hasDrawings;
    public bool HasDrawings
    {
        get => _hasDrawings;
        set
        {
            if (_hasDrawings == value) return;
            _hasDrawings = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
