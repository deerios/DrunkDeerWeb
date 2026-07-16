using DrunkDeer.Web.Theming;
using MudBlazor;

namespace DrunkDeer.Web;

/// <summary>
/// The app's MudBlazor palette: a dark, near-neutral surface stack with a single accent hue on top.
/// </summary>
/// <remarks>
/// <para>
/// The surfaces and the accent's shape are a port of the Zits UI dark theme (https://zitsui.dev,
/// neutral base, <c>md</c> radius) — its "neutral ramp, one brand hue" structure is what the design
/// asks for. Only the accent's hue is ours to choose, and <see cref="Services.ThemeService"/> takes
/// it from the keyboard.
/// </para>
/// <para>
/// Tokens are declared in OKLCH because that is how Zits authors them, so they can be read straight
/// across against the original. MudBlazor's palette parses hex only, so they are converted here —
/// see <see cref="Oklch2Srgb"/>.
/// </para>
/// </remarks>
public static class Theme
{
    // Zits' neutral gray ramp. Chroma 0 throughout: a true grey, which is what lets a single accent
    // hue carry the whole picture without fighting a tinted background.
    private static readonly Oklch Gray50 = new(0.985, 0, 0);
    private static readonly Oklch Gray400 = new(0.708, 0, 0);
    private static readonly Oklch Gray800 = new(0.269, 0, 0);
    private static readonly Oklch Gray900 = new(0.205, 0, 0);
    private static readonly Oklch Gray950 = new(0.145, 0, 0);

    /// <summary>
    /// Lightness and chroma every accent is built at, from Zits' dark-mode brand colours.
    /// </summary>
    /// <remarks>
    /// Holding these fixed and varying only the hue is what keeps an accent taken off the keyboard
    /// usable. The raw colour of a key is not: at full saturation and value a yellow would glare and
    /// a blue would sink into the background, because neither says anything about perceived
    /// lightness. Pinning L and C means every hue lands at the same weight against the surfaces.
    /// </remarks>
    public const double AccentLightness = 0.637;

    /// <inheritdoc cref="AccentLightness"/>
    public const double AccentChroma = 0.237;

    /// <summary>
    /// The accent hue used when the keyboard has no colours worth reading — Zits' red, and the red
    /// half of the red-and-black default.
    /// </summary>
    public const double DefaultAccentHue = 25.331;

    /// <summary>Builds the accent for a hue angle, at the theme's fixed lightness and chroma.</summary>
    public static string Accent(double hue) =>
        Oklch2Srgb.ToSrgb(new Oklch(AccentLightness, AccentChroma, hue)).ToHex();

    /// <summary>The palette with the default red accent.</summary>
    public static readonly MudTheme Default = Build(DefaultAccentHue);

    /// <summary>
    /// Builds the palette around one accent hue.
    /// </summary>
    /// <param name="accentHue">Drives the accent: buttons, highlights, the pressed key fill.</param>
    public static MudTheme Build(double accentHue) => new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = Accent(accentHue),
            PrimaryContrastText = Hex(Gray50),

            // Muted grey, not a second brand colour. This app uses Color.Secondary for caption and
            // help text throughout, so an accent here paints most of the prose in the UI — and an
            // accent that follows the keyboard would paint it red on a red board, which reads as
            // forty-odd error messages. Zits maps its own muted foreground to the same grey step.
            Secondary = Hex(Gray400),
            SecondaryContrastText = Hex(Gray950),

            Background = Hex(Gray950),
            BackgroundGray = Hex(Gray900),
            Surface = Hex(Gray900),
            AppbarBackground = Hex(Gray900),
            AppbarText = Hex(Gray50),
            DrawerBackground = Hex(Gray900),
            DrawerText = Hex(Gray50),
            DrawerIcon = Hex(Gray400),

            TextPrimary = Hex(Gray50),
            TextSecondary = Hex(Gray400),
            TextDisabled = Hex(Gray800),
            ActionDefault = Hex(Gray400),
            ActionDisabled = Hex(Gray800),
            ActionDisabledBackground = Hex(Gray800),

            LinesDefault = Border,
            LinesInputs = Input,
            Divider = Border,
            DividerLight = Border,
            TableLines = Border,
            
            Info = Accent(accentHue),

            Success = Oklch2Srgb.ToSrgb(new Oklch(0.723, 0.219, 149.579)).ToHex(),
            Warning = Oklch2Srgb.ToSrgb(new Oklch(0.828, 0.189, 84.429)).ToHex(),
            Error = Oklch2Srgb.ToSrgb(new Oklch(0.637, 0.237, 25.331)).ToHex(),
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "0.625rem",
        },
    };

    /// <summary>Zits' dark-mode border: white at 10%.</summary>
    private const string Border = "#ffffff1a";

    /// <summary>Zits' dark-mode input edge: white at 15%.</summary>
    private const string Input = "#ffffff26";

    private static string Hex(Oklch c) => Oklch2Srgb.ToSrgb(c).ToHex();
}
