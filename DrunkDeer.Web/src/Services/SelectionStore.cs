namespace DrunkDeer.Web.Services;

/// <summary>How an incoming set of keys combines with the current selection.</summary>
public enum SelectionMode
{
    /// <summary>The incoming keys become the selection.</summary>
    Replace,

    /// <summary>The incoming keys are added to the selection.</summary>
    Add,

    /// <summary>Each incoming key is selected if it wasn't, deselected if it was.</summary>
    Toggle,
}

/// <summary>
/// The set of keys the user has selected on the on-screen keyboard, as firmware slot indices.
/// The keyboard is the app's selection surface and the side panels (actuation, lighting, keymap)
/// edit whatever is selected here — see FUTURE_PLAN §5.4.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="KeyboardStore"/> on purpose: selection changes on every click, and
/// the things watching connection state (the app bar, the nav) have no reason to re-render for it.
/// Selection is still slow-changing enough for ordinary Blazor rendering — only per-key travel
/// needs the JS hot path.
/// </remarks>
public sealed class SelectionStore
{
    private readonly HashSet<int> _slots = [];

    /// <summary>The selected firmware slot indices, in no particular order.</summary>
    public IReadOnlyCollection<int> Slots => _slots;

    public int Count => _slots.Count;

    public bool IsEmpty => _slots.Count == 0;

    public bool Contains(int slot) => _slots.Contains(slot);

    /// <summary>Raised only when the selection actually changed.</summary>
    public event Action? Changed;

    /// <summary>Combines <paramref name="slots"/> into the selection according to <paramref name="mode"/>.</summary>
    public void Apply(SelectionMode mode, IReadOnlyCollection<int> slots)
    {
        var changed = mode switch
        {
            SelectionMode.Replace => ReplaceCore(slots),
            SelectionMode.Add => AddCore(slots),
            SelectionMode.Toggle => ToggleCore(slots),
            _ => false,
        };
        if (changed) Changed?.Invoke();
    }

    public void SelectAll(IReadOnlyCollection<int> slots) => Apply(SelectionMode.Replace, slots);

    public void Clear()
    {
        if (_slots.Count == 0) return;
        _slots.Clear();
        Changed?.Invoke();
    }

    private bool ReplaceCore(IReadOnlyCollection<int> slots)
    {
        if (_slots.SetEquals(slots)) return false;
        _slots.Clear();
        foreach (var s in slots) _slots.Add(s);
        return true;
    }

    private bool AddCore(IReadOnlyCollection<int> slots)
    {
        var changed = false;
        foreach (var s in slots) changed |= _slots.Add(s);
        return changed;
    }

    private bool ToggleCore(IReadOnlyCollection<int> slots)
    {
        var changed = false;
        foreach (var s in slots)
        {
            if (!_slots.Remove(s)) _slots.Add(s);
            changed = true;
        }
        return changed;
    }
}
