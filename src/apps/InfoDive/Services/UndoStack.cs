namespace InfoDive.Services;

/// <summary>
/// A reversible user action. Implementations capture enough state to both undo and
/// redo the action later. Keep the captured state small (the action may live in memory
/// until the stack is cleared).
/// </summary>
public interface IUndoableAction
{
    /// <summary>Short, user-visible description ("シーン「○○」の削除" etc.).</summary>
    string Description { get; }

    /// <summary>Reverse the action. Should restore observable state (collections, UI).</summary>
    void Undo();

    /// <summary>Re-apply the action after an Undo.</summary>
    void Redo();
}

/// <summary>
/// Two-stack undo/redo manager. <see cref="Push"/> records a newly performed action
/// (and discards any pending redo). <see cref="Undo"/> pops from undo → pushes to redo.
/// Bounded to <see cref="MaxDepth"/> entries; oldest are dropped.
/// </summary>
public sealed class UndoStack
{
    private readonly LinkedList<IUndoableAction> _undo = new();
    private readonly Stack<IUndoableAction> _redo = new();

    public int MaxDepth { get; set; } = 50;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? NextUndoDescription => _undo.Last?.Value.Description;
    public string? NextRedoDescription => _redo.Count > 0 ? _redo.Peek().Description : null;

    public void Push(IUndoableAction action)
    {
        _undo.AddLast(action);
        _redo.Clear();
        while (_undo.Count > MaxDepth)
            _undo.RemoveFirst();
    }

    public IUndoableAction? Undo()
    {
        if (_undo.Last == null) return null;
        var a = _undo.Last.Value;
        _undo.RemoveLast();
        a.Undo();
        _redo.Push(a);
        return a;
    }

    public IUndoableAction? Redo()
    {
        if (_redo.Count == 0) return null;
        var a = _redo.Pop();
        a.Redo();
        _undo.AddLast(a);
        return a;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
