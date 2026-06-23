using System.Collections.ObjectModel;
using InfoDive.Models;

namespace InfoDive.Services;

/// <summary>
/// Immutable snapshot of a <see cref="Keyframe"/>'s mutable properties.
/// Captures just enough state to restore after an in-place edit.
/// </summary>
internal sealed record KeyframeSnapshot(
    string Name,
    string ImageId,
    double Tx,
    double Ty,
    double Scale,
    int DurationMs,
    string Easing,
    int DwellTimeMs,
    double RefCanvasW,
    double RefCanvasH,
    ImageTransition ImageTransition)
{
    public static KeyframeSnapshot From(Keyframe kf) => new(
        kf.Name, kf.ImageId, kf.Tx, kf.Ty, kf.Scale,
        kf.DurationMs, kf.Easing, kf.DwellTimeMs,
        kf.RefCanvasW, kf.RefCanvasH, kf.ImageTransition);

    public void ApplyTo(Keyframe kf)
    {
        kf.Name = Name;
        kf.ImageId = ImageId;
        kf.Tx = Tx;
        kf.Ty = Ty;
        kf.Scale = Scale;
        kf.DurationMs = DurationMs;
        kf.Easing = Easing;
        kf.DwellTimeMs = DwellTimeMs;
        kf.RefCanvasW = RefCanvasW;
        kf.RefCanvasH = RefCanvasH;
        kf.ImageTransition = ImageTransition;
    }
}

/// <summary>
/// Generic edit action: captures before/after snapshots of a keyframe's state.
/// Suits any in-place property change (name, view, duration, transition…) that
/// doesn't need custom Undo logic.
/// </summary>
public sealed class EditKeyframeAction : IUndoableAction
{
    private readonly Keyframe _kf;
    private readonly KeyframeSnapshot _before;
    private readonly KeyframeSnapshot _after;
    private readonly string _description;

    internal EditKeyframeAction(Keyframe kf, KeyframeSnapshot before, KeyframeSnapshot after, string description)
    {
        _kf = kf;
        _before = before;
        _after = after;
        _description = description;
    }

    public string Description => _description;
    public void Undo() => _before.ApplyTo(_kf);
    public void Redo() => _after.ApplyTo(_kf);

    /// <summary>
    /// Convenience factory: snapshot → run the mutation → snapshot → create action.
    /// Returns the action (caller pushes to the stack). Returns null if nothing changed.
    /// </summary>
    public static EditKeyframeAction? Capture(Keyframe kf, string description, Action mutate)
    {
        var before = KeyframeSnapshot.From(kf);
        mutate();
        var after = KeyframeSnapshot.From(kf);
        return before == after ? null : new EditKeyframeAction(kf, before, after, description);
    }
}

/// <summary>Add a keyframe; undo removes it. Redo re-inserts at the original index.</summary>
public sealed class AddKeyframeAction : IUndoableAction
{
    private readonly ObservableCollection<Keyframe> _list;
    private readonly Keyframe _kf;
    private readonly int _index;

    public AddKeyframeAction(ObservableCollection<Keyframe> list, Keyframe kf, int index)
    {
        _list = list; _kf = kf; _index = index;
    }

    public string Description => $"シーン「{_kf.Name}」の追加";

    public void Undo() => _list.Remove(_kf);
    public void Redo() => _list.Insert(Math.Min(_index, _list.Count), _kf);
}

/// <summary>Move a keyframe from one position to another; undo restores the original.</summary>
public sealed class MoveKeyframeAction : IUndoableAction
{
    private readonly ObservableCollection<Keyframe> _list;
    private readonly Keyframe _kf;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public MoveKeyframeAction(ObservableCollection<Keyframe> list, Keyframe kf, int fromIndex, int toIndex)
    {
        _list = list; _kf = kf; _fromIndex = fromIndex; _toIndex = toIndex;
    }

    public string Description => $"シーン「{_kf.Name}」の並び替え";

    public void Undo() => _list.Move(_toIndex, _fromIndex);
    public void Redo() => _list.Move(_fromIndex, _toIndex);
}

/// <summary>Delete a single keyframe; undo re-inserts at the original index.</summary>
public sealed class DeleteKeyframeAction : IUndoableAction
{
    private readonly ObservableCollection<Keyframe> _list;
    private readonly Keyframe _kf;
    private readonly int _index;

    public DeleteKeyframeAction(ObservableCollection<Keyframe> list, Keyframe kf, int index)
    {
        _list = list; _kf = kf; _index = index;
    }

    public string Description => $"シーン「{_kf.Name}」の削除";

    public void Undo() => _list.Insert(Math.Min(_index, _list.Count), _kf);
    public void Redo() => _list.Remove(_kf);
}

/// <summary>Clear every keyframe at once (e.g., 編集→すべてのシーンを削除).</summary>
public sealed class ClearAllKeyframesAction : IUndoableAction
{
    private readonly ObservableCollection<Keyframe> _list;
    private readonly List<Keyframe> _removed;

    public ClearAllKeyframesAction(ObservableCollection<Keyframe> list, IEnumerable<Keyframe> removed)
    {
        _list = list;
        _removed = removed.ToList();
    }

    public string Description => $"全シーン ({_removed.Count} 件) の削除";

    public void Undo()
    {
        foreach (var kf in _removed) _list.Add(kf);
    }
    public void Redo() => _list.Clear();
}

/// <summary>Move an image within the images list; undo restores the original position.</summary>
public sealed class MoveImageAction : IUndoableAction
{
    private readonly ObservableCollection<LoadedImage> _list;
    private readonly LoadedImage _img;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public MoveImageAction(ObservableCollection<LoadedImage> list, LoadedImage img, int fromIndex, int toIndex)
    {
        _list = list; _img = img; _fromIndex = fromIndex; _toIndex = toIndex;
    }

    public string Description => $"画像「{_img.FileName}」の並び替え";

    public void Undo() => _list.Move(_toIndex, _fromIndex);
    public void Redo() => _list.Move(_fromIndex, _toIndex);
}

/// <summary>
/// Delete an image and the keyframes that referenced it. Undo restores the image at its
/// original index plus each linked keyframe at its original index.
/// </summary>
public sealed class DeleteImageAction : IUndoableAction
{
    private readonly ObservableCollection<LoadedImage> _images;
    private readonly ObservableCollection<Keyframe> _keyframes;
    private readonly LoadedImage _img;
    private readonly int _imgIndex;
    private readonly List<(Keyframe Kf, int Index)> _linkedKfs;
    private readonly Action<string> _setActiveImage;

    public DeleteImageAction(
        ObservableCollection<LoadedImage> images,
        ObservableCollection<Keyframe> keyframes,
        LoadedImage img, int imgIndex,
        IEnumerable<(Keyframe kf, int index)> linkedKfs,
        Action<string> setActiveImage)
    {
        _images = images;
        _keyframes = keyframes;
        _img = img;
        _imgIndex = imgIndex;
        _linkedKfs = linkedKfs.OrderBy(t => t.index).ToList();
        _setActiveImage = setActiveImage;
    }

    public string Description => $"画像「{_img.FileName}」の削除"
        + (_linkedKfs.Count > 0 ? $" + 関連シーン {_linkedKfs.Count} 件" : "");

    public void Undo()
    {
        _images.Insert(Math.Min(_imgIndex, _images.Count), _img);
        foreach (var (kf, idx) in _linkedKfs)
            _keyframes.Insert(Math.Min(idx, _keyframes.Count), kf);
        _setActiveImage(_img.Id);
    }

    public void Redo()
    {
        foreach (var (kf, _) in _linkedKfs)
            _keyframes.Remove(kf);
        _images.Remove(_img);
    }
}
