using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Services;

/// <summary>One board test mode can pretend to be: a model+variant pair and a name to show.</summary>
/// <param name="Slug">The model slug, as in <see cref="ModelSlugs"/>.</param>
/// <param name="Variant">The variant, as the identity table spells it (e.g. "ansi", "iso", "jp").</param>
/// <param name="Name">What to call it in the picker.</param>
public sealed record TestBoard(string Slug, string Variant, string Name)
{
    /// <summary>Whether this board has an on-screen layout, rather than drawing as "no layout yet".</summary>
    /// <remarks>
    /// Asked of the SDK rather than recorded here, so the picker cannot claim a board is
    /// unsupported after it gains geometry — the label follows the shipped data by construction.
    /// </remarks>
    public bool HasGeometry => KeyGeometry.TryGetKeys(Slug, Variant, out _);

    /// <summary>Whether a stored (slug, variant) choice names this board.</summary>
    public bool Matches(string? slug, string? variant) =>
        Slug == slug && Variant == (variant ?? TestBoards.DefaultVariant);
}

/// <summary>
/// The boards offered by test mode, in the order they appear in the picker.
/// </summary>
/// <remarks>
/// <para>
/// This repeats what the SDK's model identity table already knows, because that table is private
/// and <see cref="ModelRegistry"/> exposes no way to enumerate it — only <c>GetInfo(slug)</c>.
/// The duplication is small and this is a developer tool, so it is not worth a new SDK API and the
/// release that would carry it. It cannot silently drift into a lie: a slug or variant that stops
/// resolving is caught by the tests, and by the fallback in
/// <see cref="KeyboardService.ConnectDemoAsync"/> at runtime.
/// </para>
/// <para>
/// Every entry is a real (slug, variant) identity the SDK can resolve, including the ones with no
/// geometry — being able to look at the unsupported-board state on purpose is worth having. The
/// A75's "ansi_alt" is left out: it is a second PID for the same ANSI hardware and would draw an
/// identical board.
/// </para>
/// </remarks>
public static class TestBoards
{
    /// <summary>The variant assumed when a stored choice names no variant.</summary>
    public const string DefaultVariant = "ansi";

    public static readonly IReadOnlyList<TestBoard> All =
    [
        new(ModelSlugs.A75,       "ansi", "A75"),
        new(ModelSlugs.A75,       "iso",  "A75 (ISO)"),
        new(ModelSlugs.A75Pro,    "ansi", "A75 Pro"),
        new(ModelSlugs.A75Ultra,  "ansi", "A75 Ultra"),
        new(ModelSlugs.A75Master, "ansi", "A75 Master"),
        new(ModelSlugs.G75,       "ansi", "G75"),
        new(ModelSlugs.G75Jp,     "jp",   "G75 JP"),
        new(ModelSlugs.G65,       "ansi", "G65"),
        new(ModelSlugs.G65Lite,   "ansi", "G65 Lite"),
        new(ModelSlugs.G65M1,     "ansi", "G65 m1"),
        new(ModelSlugs.G65M2,     "ansi", "G65 m2"),
        new(ModelSlugs.G65M3,     "ansi", "G65 m3"),
        new(ModelSlugs.G60,       "ansi", "G60"),
        new(ModelSlugs.G60V600,   "ansi", "G60 V600"),
        new(ModelSlugs.X60Future, "ansi", "X60 Future"),
    ];

    /// <summary>The board a stored choice names, or null for "the default A75".</summary>
    public static TestBoard? Find(string? slug, string? variant) =>
        slug is null ? null : All.FirstOrDefault(b => b.Matches(slug, variant));
}
