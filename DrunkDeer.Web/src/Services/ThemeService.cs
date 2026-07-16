using DrunkDeer.Web.Theming;
using MudBlazor;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Keeps the app's accent colour in step with the colours on the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// The board's colours are run through <see cref="HueStretch"/>, which lays their hues out across a
/// palette in proportion to how many keys wear each one. Reading that palette at a quantile
/// therefore gives a hue weighted by key count rather than by whichever key happened to be looked
/// at first — the accent follows the board's bulk, and a single stray green key can't drag the
/// whole UI green.
/// </para>
/// <para>
/// A board wearing one colour — the red-and-black default — stretches to a palette of that one hue,
/// so the median is simply that colour. The stretch only has anything to say once a profile carries
/// several hues, where it decides which of them the accent should be.
/// </para>
/// <para>
/// Only the hue is taken. Lightness and chroma are the theme's, fixed — see
/// <see cref="Theme.AccentLightness"/> for why.
/// </para>
/// </remarks>
public sealed class ThemeService : IDisposable
{
    /// <summary>
    /// Where the palette is read for the accent. The median, so the accent is the colour most of
    /// the lit keys are actually wearing.
    /// </summary>
    private const double AccentQuantile = 0.5;

    /// <summary>
    /// Below this chroma a palette entry carries no hue worth reading. Catches the all-white
    /// palette <see cref="HueStretch"/> returns when no key clears its thresholds.
    /// </summary>
    private const double MinUsableChroma = 0.02;

    private readonly KeyboardService _keyboard;
    private readonly KeyboardStore _store;

    public ThemeService(KeyboardService keyboard, KeyboardStore store)
    {
        _keyboard = keyboard;
        _store = store;

        _keyboard.LightingChanged += Refresh;

        // Disconnecting drops the colour shadow without raising a lighting change, so the accent
        // would otherwise keep the hue of a board that is no longer plugged in. The store is what
        // reports the connection going away.
        _store.Changed += Refresh;
    }

    /// <summary>The palette to hand <c>MudThemeProvider</c>.</summary>
    public MudTheme Current { get; private set; } = Theme.Default;

    /// <summary>Raised when <see cref="Current"/> becomes a different palette.</summary>
    public event Action? Changed;

    /// <summary>Recomputes the accent from the board, and raises <see cref="Changed"/> if it moved.</summary>
    public void Refresh()
    {
        var rebuilt = Theme.Build(AccentHue());

        // MudThemeProvider regenerates its stylesheet whenever the theme object changes, and the
        // events feeding this fire on things that often leave the colours alone. Compare before
        // publishing so an unrelated connection change doesn't restyle the app for nothing.
        if (rebuilt.PaletteDark.Primary == Current.PaletteDark.Primary) return;

        Current = rebuilt;
        Changed?.Invoke();
    }

    /// <summary>
    /// The hue to build the accent from, falling back to the theme's default red whenever the
    /// board has nothing trustworthy to say.
    /// </summary>
    private double AccentHue()
    {
        const double fallback = Theme.DefaultAccentHue;

        // Until this session has written every key, the colour shadow is the SDK's invented seed
        // rather than anything the board reported, and theming off it would be a guess wearing a
        // confident face. The A75 cannot be asked what colour it is.
        //
        // Belt and braces as things stand: the seed is black, and a black board yields no hue and
        // falls back anyway. It is kept because the actuation shadow is seeded with a made-up
        // 2.0 mm, so a seeded colour shadow is not a far-fetched change — and this is the only
        // thing that would notice.
        if (!_keyboard.ColorsAreKnown) return fallback;

        var lit = _keyboard.SnapshotColors()
            .Select(c => FullValue(new Srgb(c.R, c.G, c.B)))
            .ToList();

        if (lit.Count == 0) return fallback;

        // A palette with no hue in it means the board is black, or grey. Either way there is no
        // colour to follow.
        return HueAt(HueStretch.Stretch(lit), AccentQuantile) ?? fallback;
    }

    /// <summary>The hue of the palette entry at <paramref name="quantile"/>, or null if it has none.</summary>
    /// <remarks>
    /// Read in OKLCH, which is the space the accent is rebuilt in. The stretch itself works in HSV,
    /// faithfully to the algorithm it ports, but the two spaces do not agree on what a hue angle
    /// means: sRGB blue is 240° in HSV and 264° in OKLCH, red 0° and 29°. Carrying the HSV number
    /// straight into an OKLCH accent would land roughly 25° away from the colour on the board — the
    /// right hue by arithmetic and the wrong one by eye.
    /// </remarks>
    private static double? HueAt(Srgb[] palette, double quantile)
    {
        int index = Math.Clamp((int)(palette.Length * quantile), 0, palette.Length - 1);
        var entry = Oklch2Srgb.FromSrgb(palette[index]);
        return entry.C < MinUsableChroma ? null : entry.H;
    }

    /// <summary>
    /// Turns a key's colour up to full brightness, keeping its hue and saturation, so that how
    /// brightly a key is lit has no bearing on the hue read from it. Black stays black.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because the stored colours are not on a common scale. The board has one brightness
    /// for the whole surface, and the SDK folds it into the shadow unevenly: a theme's base colour
    /// is stored already dimmed, while the per-key overrides written over it are stored at full
    /// strength. So the same blue board can hold both (0, 0, 28) and (0, 0, 255) at once, and the
    /// stretch's value threshold would throw away the first as though those keys were unlit — half
    /// the board silently losing its vote.
    /// </para>
    /// <para>
    /// Scaling each colour by its own brightest channel fixes that without needing to know what the
    /// brightness was, or which of the two ways any given key was written. It is a uniform scale
    /// per colour, so hue and saturation come through untouched, and it cannot clip: the brightest
    /// channel lands exactly on 255. Undoing the board brightness instead would have to clamp, and
    /// clamping is not uniform — a full-strength orange multiplied up and clipped comes back
    /// yellow, which is precisely the corruption this code exists to avoid.
    /// </para>
    /// <para>
    /// The thresholds in <see cref="HueStretch"/> then do what they should: reject a key that is
    /// off or grey, rather than one that is merely turned down.
    /// </para>
    /// </remarks>
    private static Srgb FullValue(Srgb c)
    {
        byte max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max == 0) return c;

        return new Srgb(Channel(c.R), Channel(c.G), Channel(c.B));

        byte Channel(byte v) => (byte)Math.Min(v * 255 / max, 255);
    }

    public void Dispose()
    {
        _keyboard.LightingChanged -= Refresh;
        _store.Changed -= Refresh;
    }
}
