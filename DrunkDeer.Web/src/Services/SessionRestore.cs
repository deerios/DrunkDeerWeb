using DrunkDeer.Protocol;
using Microsoft.Extensions.Logging;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Puts settings back on the board when it connects, and — when asked to — keeps a running copy of
/// the board's settings so there is something to put back.
/// </summary>
/// <remarks>
/// Both halves of <see cref="StartupAction"/> live here because they are two ends of the same rope:
/// what <see cref="StartupAction.LastChanges"/> restores is exactly what the autosave below writes,
/// and splitting them would leave the format agreed across two files.
/// <para>
/// The restore is not run from here. It writes a whole board's worth of settings to real hardware,
/// so it is triggered by the page that connected the keyboard — see <c>Connect.razor</c> — which is
/// where there is a user to tell about it and somewhere to put an error. This service only knows
/// how.
/// </para>
/// </remarks>
public sealed class SessionRestore : IDisposable
{
    private const string StorageKey = "drunkdeer.lastchanges";

    /// <summary>
    /// How long the board has to sit still before its settings are written to storage.
    /// </summary>
    /// <remarks>
    /// Dragging the brightness slider is one gesture but a stream of writes, and each one raises a
    /// change. Long enough to collapse a gesture into one save; short enough that closing the tab
    /// straight after an edit still catches it.
    /// </remarks>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(750);

    private readonly KeyboardService _keyboard;
    private readonly KeyboardStore _store;
    private readonly ProfileLibrary _profiles;
    private readonly ActiveProfile _active;
    private readonly SettingsService _settings;
    private readonly BrowserStorage _storage;
    private readonly ILogger<SessionRestore> _log;

    private CancellationTokenSource? _pendingSave;

    public SessionRestore(
        KeyboardService keyboard, KeyboardStore store, ProfileLibrary profiles,
        ActiveProfile active, SettingsService settings, BrowserStorage storage,
        ILogger<SessionRestore> log)
    {
        _keyboard = keyboard;
        _store = store;
        _profiles = profiles;
        _active = active;
        _settings = settings;
        _storage = storage;
        _log = log;

        // Every way the board's settings can change, since any of them is a change worth keeping.
        _keyboard.LightingChanged += OnBoardChanged;
        _keyboard.ActuationChanged += OnBoardChanged;
        // Rapid trigger is the one setting the board reports itself, so KeyboardService has no
        // "known" flag for it — this is that flag. A restored session should come back with it as
        // the user left it, not as the firmware happens to have it.
        _keyboard.RapidTriggerChanged += OnRapidTriggerChanged;

        // What the session knew dies with the board, and the next one starts from the SDK's
        // invented seed again (see KeyboardService.ResetShadowState). This flag has to go with it.
        _store.Changed += OnConnectionChanged;
    }

    /// <summary>Whether the user has switched rapid trigger in this session.</summary>
    private bool _rapidTriggerSet;

    private void OnRapidTriggerChanged()
    {
        _rapidTriggerSet = true;
        OnBoardChanged();
    }

    private void OnConnectionChanged()
    {
        if (!_store.IsConnected) _rapidTriggerSet = false;
    }

    /// <summary>
    /// Whether this session has set anything that would be worth putting back.
    /// </summary>
    /// <remarks>
    /// Not every change event is a change to the board. Moving the actuation slider publishes a
    /// preview so the on-screen board can show where the depth is heading, and selecting a key alone
    /// is enough to raise it — nothing has been written, and there is nothing to save. Without this,
    /// clicking a key would store a profile holding only the rapid trigger flag the board reported
    /// at connect, and the next startup would announce it had restored something when it hadn't.
    /// <para>
    /// These are the same flags <see cref="KeyboardService.CaptureProfile"/> gates itself on, which
    /// is what makes them the right question: if they are all false, a capture has nothing in it
    /// that the user chose.
    /// </para>
    /// </remarks>
    private bool WorthSaving =>
        _keyboard.DepthsAreKnown || _keyboard.SensitivityIsKnown || _keyboard.ColorsAreKnown || _rapidTriggerSet;

    // ── Restoring, at connect ────────────────────────────────────────────────

    /// <summary>
    /// Does whatever <see cref="AppSettings.Startup"/> asks for, and returns what it did — or null
    /// if it was asked for nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// There was something to restore and it couldn't be: the profile has been deleted since it was
    /// chosen, or the stored snapshot no longer parses.
    /// </exception>
    public async Task<string?> ApplyStartupAsync(CancellationToken ct = default)
    {
        if (!_store.IsConnected) return null;

        return _settings.Current.Startup switch
        {
            StartupAction.Profile => await ApplyStartupProfileAsync(ct).ConfigureAwait(false),
            StartupAction.LastChanges => await ApplyLastChangesAsync(ct).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<string?> ApplyStartupProfileAsync(CancellationToken ct)
    {
        var name = _settings.Current.StartupProfile;
        // No profile chosen is a half-finished setting, not a fault — the settings page lets the
        // action be picked before the profile is.
        if (!ProfileLibrary.IsValidName(name)) return null;

        var profile = await _profiles.LoadAsync(name!).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"'{name}' was set to load at startup, but it isn't saved in this browser any more.");

        await _keyboard.ApplyProfileAsync(profile, ct).ConfigureAwait(false);
        // The board really is wearing this profile now, so the Profiles panel can offer to save
        // changes straight back to it — which is the whole point of loading one at startup.
        _active.Set(name!);
        return $"Loaded '{name}'.";
    }

    private async Task<string?> ApplyLastChangesAsync(CancellationToken ct)
    {
        var json = await _storage.GetAsync(StorageKey).ConfigureAwait(false);
        // Nothing saved yet: the setting was turned on and this is the first connection since. There
        // is nothing to say about it.
        if (string.IsNullOrEmpty(json)) return null;

        var profile = ProfileLibrary.Import(json);
        await _keyboard.ApplyProfileAsync(profile, ct).ConfigureAwait(false);
        // Deliberately no ActiveProfile: these changes belong to no saved profile. That is what
        // makes them "last changes" rather than a profile.
        return "Restored your last changes.";
    }

    // ── Saving, as the board changes ─────────────────────────────────────────

    private void OnBoardChanged()
    {
        if (_settings.Current.Startup != StartupAction.LastChanges) return;
        if (!_store.IsConnected || !WorthSaving) return;

        var previous = _pendingSave;
        var cts = new CancellationTokenSource();
        _pendingSave = cts;
        previous?.Cancel();
        previous?.Dispose();

        _ = SaveSoonAsync(cts);
    }

    private async Task SaveSoonAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SaveDelay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Another change arrived inside the window, and it owns the save now.
            return;
        }

        try
        {
            // Re-checked rather than assumed: the board can be unplugged inside the delay, and the
            // session's picture of it is torn down when that happens. Capturing then would either
            // throw or store the SDK's seed as though it were the user's settings.
            if (!_store.IsConnected || !WorthSaving) return;
            await _storage.SetAsync(StorageKey, _keyboard.CaptureProfile().ToJson()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing is waiting on this and there is nothing the user could do about it. The next
            // change tries again; the cost of failing is opening with older settings.
            _log.LogWarning(ex, "Couldn't save the session's last changes.");
        }
    }

    /// <summary>
    /// Stores <paramref name="profile"/> as the last changes, whatever the settings say.
    /// </summary>
    /// <remarks>
    /// For the settings page: turning the option on with a keyboard already connected should have
    /// something to restore, rather than waiting for the next edit before it means anything.
    /// </remarks>
    public Task SaveNowAsync(KeyboardProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _storage.SetAsync(StorageKey, profile.ToJson());
    }

    public void Dispose()
    {
        _keyboard.LightingChanged -= OnBoardChanged;
        _keyboard.ActuationChanged -= OnBoardChanged;
        _keyboard.RapidTriggerChanged -= OnRapidTriggerChanged;
        _store.Changed -= OnConnectionChanged;
        _pendingSave?.Cancel();
        _pendingSave?.Dispose();
        _pendingSave = null;
    }
}
