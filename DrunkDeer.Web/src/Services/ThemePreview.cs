using DrunkDeer.Protocol;
using Microsoft.Extensions.Logging;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Puts a gallery theme on the real keyboard for a few seconds and then puts the board back.
/// </summary>
/// <remarks>
/// A thumbnail can only ever be an impression: the point of a preview is the light in the room.
/// Writing the theme permanently to see it would mean the user had to undo a theme they were only
/// looking at, so it is written, held, and taken back off.
/// <para>
/// A singleton rather than per-card state because a preview outlives the card that started it —
/// navigating away mid-preview must still put the board back, and it is the board, not the card,
/// that only has one of. That is also why only one preview runs at a time: starting a second while
/// the first is up would capture the first theme as the state to "restore" to, and the board would
/// never find its way home.
/// </para>
/// </remarks>
public sealed class ThemePreview : IAsyncDisposable
{
    /// <summary>How long a theme stays on the board. Long enough to look up at it, short enough to wait out.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(5);

    private readonly KeyboardService _keyboard;
    private readonly ILogger<ThemePreview> _log;
    private CancellationTokenSource? _cts;
    private KeyboardService.LightingRestorePoint? _restore;

    public ThemePreview(KeyboardService keyboard, ILogger<ThemePreview> log)
    {
        _keyboard = keyboard;
        _log = log;
    }

    /// <summary>The theme on the board right now, or null when no preview is running.</summary>
    public GalleryTheme? Active { get; private set; }

    /// <summary>Raised when a preview starts or ends, so the cards can show which one is up.</summary>
    public event Action? Changed;

    /// <summary>Whether a preview can be started at all: there has to be a keyboard to put it on.</summary>
    public bool CanPreview => _keyboard.IsConnected;

    /// <summary>
    /// Whether the board's current lighting is something this session could put back afterwards.
    /// </summary>
    /// <remarks>
    /// False before anything in this session has set the lighting: the keyboard cannot report its
    /// own lighting back, so there is nothing to return to. The preview still works — it just ends
    /// with the theme still on the board, and the user is told that up front rather than finding
    /// out when the countdown runs out and nothing changes.
    /// </remarks>
    public bool CanRestore => _keyboard.IsConnected && _keyboard.CaptureLighting().CanRestore;

    /// <summary>
    /// Writes <paramref name="theme"/> to the board and schedules putting it back in
    /// <see cref="Duration"/>. Any preview already running is ended first.
    /// </summary>
    public async Task StartAsync(GalleryTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!_keyboard.IsConnected) throw new InvalidOperationException("Not connected.");

        // Before capturing anything: the state worth restoring is whatever was on the board before
        // any preview, and the running one is standing on top of it.
        await EndAsync().ConfigureAwait(false);

        _restore = _keyboard.CaptureLighting();
        await _keyboard.ApplyProfileAsync(new KeyboardProfile { Theme = theme.Theme }).ConfigureAwait(false);

        Active = theme;
        Changed?.Invoke();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = RunAsync(cts);
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Duration, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ended early — by another preview, by Stop, or by the page going away. Whoever
            // cancelled owns the restore, so this just steps aside.
            return;
        }

        await EndAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the running preview now, putting the board back. Does nothing if none is running.</summary>
    public Task StopAsync() => EndAsync();

    private async Task EndAsync()
    {
        var cts = _cts;
        var restore = _restore;
        _cts = null;
        _restore = null;
        if (Active is null) return;
        Active = null;

        cts?.Cancel();
        cts?.Dispose();

        try
        {
            if (restore is not null) await _keyboard.RestoreLightingAsync(restore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The board was unplugged mid-preview, most likely. There is nothing to put back to and
            // nothing the user can do, so it is logged rather than thrown at whoever happened to be
            // holding the timer.
            _log.LogWarning(ex, "Couldn't restore the lighting after previewing a theme.");
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync() => await EndAsync().ConfigureAwait(false);
}
