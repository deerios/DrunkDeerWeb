namespace DrunkDeer.Web.Services;

/// <summary>
/// Which saved profile the board is currently wearing, and whether it has been edited since.
/// </summary>
/// <remarks>
/// This is what makes "save my changes back over the profile I'm using" possible. Applying a profile
/// is otherwise a one-way trip: the settings land on the board and nothing remembers where they came
/// from, so the only way to keep an edit was to type the name again and confirm an overwrite.
/// <para>
/// Set by whoever applies a profile deliberately — <c>ProfilePanel</c> and <see cref="SessionRestore"/> —
/// rather than from inside <see cref="KeyboardService.ApplyProfileAsync"/>, because not every profile
/// write means "the user is now using this profile". A gallery preview writes one too, and it is
/// explicitly temporary.
/// </para>
/// </remarks>
public sealed class ActiveProfile : IDisposable
{
    private readonly KeyboardService _keyboard;
    private readonly KeyboardStore _store;

    /// <summary>What the board looked like the moment this profile was applied (or last saved).</summary>
    private string? _baseline;

    public ActiveProfile(KeyboardService keyboard, KeyboardStore store)
    {
        _keyboard = keyboard;
        _store = store;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>The profile in use, or null if none has been applied in this session.</summary>
    public string? Name { get; private set; }

    /// <summary>Raised when the profile in use changes, or is dropped.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whether the board has been changed since <see cref="Name"/> was applied — i.e. whether there
    /// is anything to save back.
    /// </summary>
    /// <remarks>
    /// Answered by comparing what a save would capture now against what it captured then, rather
    /// than by watching for edits. Watching would have to know which changes count: a gallery
    /// preview writes to the board and takes it straight back off, and an edit that is undone by
    /// hand leaves the board exactly as the profile had it. Comparing the two snapshots gets both
    /// right for free — if the board matches the profile, there is nothing to save, however it got
    /// back there.
    /// </remarks>
    public bool HasUnsavedChanges =>
        Name is not null && _baseline is not null && Capture() is { } now && now != _baseline;

    /// <summary>Records that the board is now wearing <paramref name="name"/>, as saved.</summary>
    /// <remarks>
    /// Call after the write has landed, not before: the baseline is read from the session, and the
    /// session only knows what the profile did to it once it has done it.
    /// </remarks>
    public void Set(string name)
    {
        Name = name;
        _baseline = Capture();
        Changed?.Invoke();
    }

    /// <summary>Follows a rename, so the profile in use doesn't become one that no longer exists.</summary>
    public void Renamed(string from, string to)
    {
        // Ordinal, matching ProfileLibrary.RenameAsync: names differing only in case are different
        // profiles, so only the one that actually moved should follow.
        if (!string.Equals(Name, from, StringComparison.Ordinal)) return;
        Name = to;
        Changed?.Invoke();
    }

    /// <summary>Drops <paramref name="name"/> if it's the one in use — it has just been deleted.</summary>
    /// <remarks>
    /// The board keeps the settings; what's gone is the place to save them back to. So this clears
    /// the name rather than pretending the profile is still there and offering to update a profile
    /// that would have to be recreated.
    /// </remarks>
    public void Forget(string name)
    {
        if (!string.Equals(Name, name, StringComparison.Ordinal)) return;
        Clear();
    }

    /// <summary>Forgets the profile in use entirely.</summary>
    public void Clear()
    {
        if (Name is null) return;
        Name = null;
        _baseline = null;
        Changed?.Invoke();
    }

    // The board going away takes the session's picture of it with it (see
    // KeyboardService.ResetShadowState), so there is nothing left to compare a profile against and
    // nothing honest to say about what the next board is wearing.
    private void OnStoreChanged()
    {
        if (!_store.IsConnected) Clear();
    }

    /// <summary>The board's settings as a save would store them, or null when there's no board.</summary>
    private string? Capture()
    {
        if (!_store.IsConnected) return null;
        try
        {
            return _keyboard.CaptureProfile().ToJson();
        }
        catch (InvalidOperationException)
        {
            // The session ended between the check above and here.
            return null;
        }
    }

    public void Dispose() => _store.Changed -= OnStoreChanged;
}
