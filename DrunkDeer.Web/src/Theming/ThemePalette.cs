using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Theming;

/// <summary>The colour every key wears under a <see cref="KeyboardTheme"/>.</summary>
/// <remarks>
/// A theme describes a board it hasn't been written to yet — a base colour plus a handful of
/// per-key overrides — so drawing one needs it expanded to a colour per key. A live session does
/// this expansion internally when a theme is applied and answers <c>GetKeyColor</c> from the
/// result; this is the same expansion for the case where there is no session to ask, which is what
/// lets the gallery draw a theme with no keyboard plugged in.
/// <para>
/// It deliberately mirrors the SDK's own apply path (<c>KeyboardSession.ApplyThemeAsync</c>): the
/// base colour is scaled by <see cref="KeyboardTheme.BaseBrightness"/> and per-key overrides land
/// on top unscaled. Getting that backwards would make the picture a lie about what the hardware
/// will do. <see cref="KeyboardTheme.Brightness"/> is not applied here for the same reason the
/// session doesn't apply it either: it is one firmware byte for the whole frame, not part of any
/// key's colour.
/// </para>
/// </remarks>
public sealed class ThemePalette
{
    private readonly (byte R, byte G, byte B) _base;
    private readonly Dictionary<DDKey, (byte R, byte G, byte B)> _overrides = [];

    public ThemePalette(KeyboardTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var baseColor = theme.BaseBrightness is { } level ? theme.BaseColor.Scale(level) : theme.BaseColor;
        _base = (baseColor.R, baseColor.G, baseColor.B);

        if (theme.Keys is null) return;
        foreach (var (name, color) in theme.Keys)
        {
            // A theme can name a key this model hasn't got — the catalog is shared across boards,
            // and one written for a full-size layout still has plenty to say about a 75%. The SDK
            // skips those on apply; skipping them here keeps the picture and the hardware agreeing.
            if (Enum.TryParse<DDKey>(name, ignoreCase: true, out var key))
                _overrides[key] = (color.R, color.G, color.B);
        }
    }

    /// <summary>The colour <paramref name="key"/> wears: its override if the theme gives one, else the base.</summary>
    public (byte R, byte G, byte B) this[DDKey key] =>
        _overrides.TryGetValue(key, out var c) ? c : _base;
}
