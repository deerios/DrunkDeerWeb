using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrunkDeer.Web.Services;

/// <summary>
/// The gallery's theme files, kept in localStorage between page loads, so that a theme already
/// looked at is not fetched again the next time somebody opens the gallery.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ThemeGallery"/> already shares a theme between the cards of one page load. This is the
/// same idea outliving the tab: the lighting is a file per theme and a page turn fetches twelve of
/// them, so browsing the gallery, closing it and coming back costs the whole page again. What is
/// stored is the file exactly as it was fetched — the bytes, not a parse of them — because the point
/// is to stand in for the fetch, and everything downstream then treats a cached theme and a fetched
/// one identically.
/// </para>
/// <para>
/// Keyed by id and version together, and that pairing is the whole design. An id used to be enough
/// on its own: theme files were written once and never rewritten, which is why
/// <see cref="ThemeGallery.IndexFetchUrl"/> busts the catalogue's caches and deliberately leaves the
/// theme files to be cached forever. Updates ended that — the file at a given id is rewritten in
/// place — so a cache keyed by id alone would answer every future load with the picture from before
/// the update, and no amount of refreshing would shift it. The version comes from the catalogue,
/// which is fetched fresh on every load, so a theme that has been updated arrives with a version no
/// stored copy answers to and the cache simply misses. That is why the catalogue is not cached: it
/// is the thing that says which cached themes are still true.
/// </para>
/// <para>
/// One key per theme rather than one per version, so an update overwrites the copy it supersedes
/// instead of leaving it behind — nothing here can enumerate localStorage to go and collect them,
/// and an origin that fills up with the last five revisions of every theme somebody ever previewed
/// is a worse bargain than the fetches it saves.
/// </para>
/// <para>
/// Nothing here is trusted, on exactly the reasoning in <see cref="ThemeGallery"/>'s notes about the
/// repository — more so, if anything: this is a string out of the user's own browser, which anybody
/// with the devtools open can write. So a stored theme is size-capped on the way out and read back
/// through <see cref="ThemeGallery.ReadThemeFile"/>, the same check the network's answer gets, and
/// anything that fails it is a miss rather than an error. Nothing here can therefore show a theme
/// that a fetch would not have.
/// </para>
/// <para>
/// A failure at either end is a miss and never a throw. <see cref="BrowserStorage"/> already swallows
/// a browser refusing site data, and a full origin — 500 themes will not all fit — makes writes stop
/// landing rather than start failing. In both cases the gallery fetches, which is what it did before
/// this existed.
/// </para>
/// </remarks>
public sealed class ThemeCache
{
    /// <summary>Where a theme is stored, before its id.</summary>
    /// <remarks>
    /// The <c>drunkdeer.</c> namespace the settings and the profiles are already in, so that what
    /// this app has put in an origin is apparent from the key list.
    /// </remarks>
    private const string KeyPrefix = "drunkdeer.theme.";

    private readonly BrowserStorage _storage;

    public ThemeCache(BrowserStorage storage) => _storage = storage;

    /// <summary>
    /// The stored file for version <paramref name="version"/> of <paramref name="id"/>, or null if
    /// there isn't one worth having.
    /// </summary>
    /// <remarks>
    /// Null covers every way this can not answer — nothing stored, storage unreadable, a stored copy
    /// of a version that has since been superseded, and something in there that is not a theme at
    /// all. The caller does the same thing about all of them, which is to fetch.
    /// </remarks>
    public async Task<string?> ReadAsync(string id, int version)
    {
        var stored = await _storage.GetAsync(KeyFor(id)).ConfigureAwait(false);
        if (stored is null) return null;

        Stored? entry;
        try
        {
            entry = JsonSerializer.Deserialize<Stored>(stored);
        }
        catch (JsonException)
        {
            // Something else's key, or a half-written value: not this method's problem to report.
            return null;
        }

        if (entry?.File is null || entry.Version != version) return null;

        // The same bound the fetch is held to, because this is standing in for a fetch. localStorage
        // is bounded per origin and not per key, so this is not about running out of memory — it is
        // that a theme too big to have been fetched must not become drawable by being put here.
        return entry.File.Length > ThemeGallery.MaxThemeBytes ? null : entry.File;
    }

    /// <summary>Stores <paramref name="file"/> as version <paramref name="version"/> of <paramref name="id"/>.</summary>
    /// <remarks>
    /// Overwrites whatever was under that id, which is how an old version stops taking up room. Does
    /// nothing at all if the browser will not have it — see the note on the class.
    /// </remarks>
    public Task WriteAsync(string id, int version, string file) =>
        _storage.SetAsync(KeyFor(id), JsonSerializer.Serialize(new Stored { Version = version, File = file }));

    /// <summary>Forgets the stored copy of <paramref name="id"/>, whatever version it is.</summary>
    public Task ForgetAsync(string id) => _storage.RemoveAsync(KeyFor(id));

    /// <remarks>
    /// Lower-cased because localStorage keys are case-sensitive and ids are compared without case
    /// everywhere else in the gallery — <see cref="ThemeGallery.ReadIndex"/> will not let two themes
    /// differing only in case into the catalogue, so folding here cannot collide two real themes,
    /// and not folding would let one theme have two entries.
    /// </remarks>
    private static string KeyFor(string id) => KeyPrefix + id.ToLowerInvariant();

    /// <summary>What actually goes in the key: the file, and which version of the theme it is.</summary>
    /// <remarks>
    /// The version travels with the file rather than in the key so that one theme is one key. The
    /// file is held as a string rather than the parsed theme because the bytes are the thing being
    /// cached; re-serialising a parse would store this app's idea of the theme instead of the
    /// repository's, and the two are only the same until one of them changes.
    /// </remarks>
    private sealed class Stored
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("file")] public string? File { get; set; }
    }
}
