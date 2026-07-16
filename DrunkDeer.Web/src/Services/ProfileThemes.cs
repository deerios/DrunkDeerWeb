using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Services;

/// <summary>
/// The user's saved profiles, read for the lighting in them: what the Publish and Modify dialogs
/// both need before they can offer a theme to send.
/// </summary>
/// <remarks>
/// <para>
/// Every profile is read when this is loaded rather than when one is picked, and that is
/// load-bearing rather than eager: both dialogs send their theme by handing the browser a real
/// <c>&lt;a href&gt;</c> (see <see cref="ThemePublisher"/>), and an href has to be right before it is
/// clicked. A profile whose lighting arrived after the click would arrive too late.
/// </para>
/// <para>
/// A profile that would not load is remembered by the reason rather than dropped, so picking it says
/// what is wrong with it instead of silently offering nothing.
/// </para>
/// </remarks>
public sealed class ProfileThemes
{
    /// <summary>One profile's lighting, or why there is none to send.</summary>
    /// <remarks>Exactly one of the two is set.</remarks>
    public readonly record struct Lighting(KeyboardTheme? Theme, string? Problem);

    private readonly Dictionary<string, Lighting> _lighting = new(StringComparer.Ordinal);

    /// <summary>Every saved profile's name, in the order the library lists them.</summary>
    public IReadOnlyList<string> Names { get; private set; } = [];

    private ProfileThemes() { }

    /// <summary>Reads every saved profile.</summary>
    public static async Task<ProfileThemes> LoadAsync(ProfileLibrary profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var read = new ProfileThemes { Names = await profiles.ListAsync().ConfigureAwait(false) };
        foreach (var name in read.Names)
            read._lighting[name] = await ReadAsync(profiles, name).ConfigureAwait(false);
        return read;
    }

    /// <summary>What <paramref name="profile"/> has to offer, or the reason it has nothing.</summary>
    public Lighting this[string profile] =>
        _lighting.TryGetValue(profile, out var known) ? known : new Lighting(null, null);

    /// <summary>Whether any saved profile has lighting in it at all.</summary>
    public bool Any => _lighting.Values.Any(l => l.Theme is not null);

    /// <summary>Which profile a dialog should open on.</summary>
    /// <param name="active">The profile currently on the keyboard, if there is one.</param>
    /// <remarks>
    /// The one on the board first: publishing or updating usually follows getting a theme right and
    /// putting it on the keyboard, so that is the one being talked about. Failing that, the first
    /// that has any lighting in it — the alternative is opening on whichever profile happens to sort
    /// first and explaining that it cannot be sent, which is a poor first impression of a feature
    /// that works.
    /// </remarks>
    public string? Pick(string? active)
    {
        if (Names.Count == 0) return null;
        if (active is not null && Names.Contains(active, StringComparer.Ordinal)) return active;
        return Names.FirstOrDefault(p => _lighting[p].Theme is not null) ?? Names[0];
    }

    private static async Task<Lighting> ReadAsync(ProfileLibrary profiles, string profile)
    {
        try
        {
            var saved = await profiles.LoadAsync(profile).ConfigureAwait(false);
            if (saved is null) return new Lighting(null, $"“{profile}” could not be found any more.");
            // A profile is allowed to be actuation only — the panels save what they know, and
            // lighting is only known once this session has written some.
            if (saved.Theme is null)
                return new Lighting(null, $"“{profile}” has no lighting saved in it, so there is no theme to send.");
            return new Lighting(saved.Theme, null);
        }
        catch (Exception ex)
        {
            return new Lighting(null, ex.Message);
        }
    }
}
