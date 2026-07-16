using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DrunkDeer.Protocol;
using Microsoft.Extensions.Logging;

namespace DrunkDeer.Web.Services;

/// <summary>
/// The catalogue of lighting themes the gallery browses, the themes themselves, and the one
/// operation that takes one out of it: copying it into the user's own profiles.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue is <c>index.json</c> in the themes repository — the same file the Publish button's
/// submissions end up in, so what a person sees here is what everyone else's submissions built. It
/// says what each theme is called and who by, and nothing about what any of them looks like: the
/// lighting is a file per theme, fetched when there is a card to draw with it. A gallery that pages
/// through hundreds of themes then costs the page it is showing rather than the whole repository,
/// which is the entire point of the arrangement.
/// </para>
/// <para>
/// Nothing that comes back is trusted — neither half. It arrives over HTTPS from GitHub, which says
/// who served it and nothing at all about whether it is any good: the checks that stand between a
/// submission and those files live in another repository, in a language this one is not written in,
/// and a repository can be wrong about its own rules. So both are read the way any other stranger's
/// input is, and what does not survive that is dropped rather than drawn. See <see cref="ReadIndex"/>
/// and <see cref="ReadThemeFile"/>.
/// </para>
/// <para>
/// There is no offline fallback and deliberately so. The themes are the repository's, so a copy kept
/// here to show when the network is down would be a second, drifting gallery that nobody publishes
/// to — and a smaller gallery that says nothing about being smaller is a worse answer than an honest
/// "this did not load". <see cref="ListAsync"/> therefore throws, and the page offers a retry.
/// </para>
/// </remarks>
public sealed partial class ThemeGallery
{
    /// <summary>Where the themes repository is served from.</summary>
    /// <remarks>
    /// raw.githubusercontent.com rather than the API: these are static files, they are served with
    /// permissive CORS, and they cost no rate limit that a browser without a token has to share.
    /// Pinned to <c>main</c> — the branch the publish workflow merges into.
    /// </remarks>
    private const string RawRoot = "https://raw.githubusercontent.com/deerios/DrunkDeerThemes/main";

    /// <summary>The catalogue every copy of the app reads.</summary>
    public const string IndexUrl = $"{RawRoot}/index.json";

    /// <summary>
    /// <see cref="IndexUrl"/> with something on the end that no cache has seen before.
    /// </summary>
    /// <remarks>
    /// raw.githubusercontent.com is served through a CDN that holds a file for minutes at a time,
    /// and the browser keeps its own copy on top of that. Neither is told when a theme is merged, so
    /// a user who publishes one and comes back to look for it is shown a catalogue from before it
    /// existed — and the Refresh button, fetching the same URL, would be handed the same stale copy
    /// and look broken. A query string both caches key on but the server ignores is what gets past
    /// them; the value only has to be one no fetch has used before.
    /// <para>
    /// A fresh value rather than a clock reading, because two refreshes inside the clock's resolution
    /// would read the same and produce the URL that was just cached — the one case this exists for.
    /// </para>
    /// <para>
    /// Only the catalogue. A theme file is written once under an id that is never reused, so a cached
    /// one is never wrong — and busting those would throw away the sharing that makes paging cheap.
    /// </para>
    /// </remarks>
    private static string IndexFetchUrl() => $"{IndexUrl}?t={Guid.NewGuid():N}";

    /// <summary>The only shape of <c>index.json</c> this knows how to read.</summary>
    /// <remarks>
    /// Set by <c>tools/lib/catalogue.mjs</c>'s <c>INDEX_VERSION</c> over there, and this is the same
    /// agreement written down in the other repository. A different number means the file has been
    /// rearranged into something these rules would misread, so it is an error rather than a guess.
    /// Version 1 carried every theme's lighting inline; 2 is the metadata this reads.
    /// </remarks>
    public const int IndexVersion = 2;

    /// <summary>The largest catalogue this will read.</summary>
    /// <remarks>
    /// An entry is around a hundred bytes now the lighting is not in it, so this is room for
    /// thousands of themes and still a bound. Anybody with a GitHub account can add to that file and
    /// nothing caps how often, so its size is not something this app gets to assume — and a browser
    /// tab that fetches until it dies is a poor way to find that out.
    /// </remarks>
    public const int MaxIndexBytes = 512 * 1024;

    /// <summary>The largest single theme file this will read.</summary>
    /// <remarks>
    /// The themes repository holds a theme's JSON to 16 KB, and the file wraps it in a record and
    /// writes it indented, which roughly triples it. This is well clear of that and still a bound,
    /// on the same reasoning as <see cref="MaxIndexBytes"/>: the file is not this app's to assume.
    /// </remarks>
    public const int MaxThemeBytes = 64 * 1024;

    /// <summary>Most themes the gallery will list.</summary>
    public const int MaxThemes = 500;

    // Mirrors tools/lib/validate.mjs in the themes repository. Those are the numbers a submission is
    // actually held to; these are this app refusing to render what it would not have accepted.
    private const int MaxThemeNameLength = 40;
    private const int MaxAuthorLength = 40;
    private const int MaxKeysPerTheme = 128;
    private const int MaxIdLength = 48;
    private const byte MaxBrightness = 9;

    private readonly ProfileLibrary _profiles;
    private readonly HttpClient _http;
    private readonly ILogger<ThemeGallery> _log;

    private Task<IReadOnlyList<GalleryEntry>>? _catalogue;

    // One entry per theme anybody has asked for, kept for the life of the page: two cards for the
    // same theme share a fetch, and paging back to where you were costs nothing. Bounded in practice
    // by MaxThemes, and a theme is a few KB.
    private readonly Dictionary<string, Task<GalleryTheme>> _themes = new(StringComparer.OrdinalIgnoreCase);

    public ThemeGallery(ProfileLibrary profiles, HttpClient http, ILogger<ThemeGallery> log)
    {
        _profiles = profiles;
        _http = http;
        _log = log;
    }

    /// <summary>Every theme in the gallery, as the catalogue lists them.</summary>
    /// <remarks>
    /// Fetched once per page load and shared by every card, since it does not change while a page is
    /// open. Throws if it cannot be had — see the note on the class about why there is nothing to
    /// fall back to — and a failure is not remembered, so asking again tries the network again
    /// rather than leaving a tab that was offline for a moment broken until it is reloaded.
    /// </remarks>
    /// <exception cref="InvalidDataException">The catalogue is not one this app can read.</exception>
    public async Task<IReadOnlyList<GalleryEntry>> ListAsync()
    {
        // Shared while it is in flight, so a page full of cards is still one fetch.
        var attempt = _catalogue ??= FetchIndexAsync();

        try
        {
            return await attempt.ConfigureAwait(false);
        }
        catch
        {
            // Forgotten once it has failed, and cleared out here rather than inside the attempt.
            // That is not a style choice: an attempt that fails without ever awaiting runs to its
            // end during the `??=` above, so clearing the field from in there would happen before
            // the assignment it is meant to undo — leaving the failure cached as the answer for
            // ever, which is the one thing this is trying to avoid.
            Forget(ref _catalogue, attempt);
            throw;
        }
    }

    /// <summary>Throws away what has been fetched, so the next ask goes to the network.</summary>
    /// <remarks>What the page's retry button is for.</remarks>
    public void Reset()
    {
        _catalogue = null;
        _themes.Clear();
    }

    /// <summary>The lighting for one catalogue entry.</summary>
    /// <remarks>
    /// Each theme is its own file, so this is one fetch per theme — and only for the themes somebody
    /// is looking at, which is what makes paging worth having. Shared and remembered per id, so two
    /// cards showing the same theme fetch once and paging back fetches not at all. A failure is
    /// forgotten rather than remembered, exactly as in <see cref="ListAsync"/>, so a card that could
    /// not load can be tried again.
    /// </remarks>
    /// <exception cref="InvalidDataException">The theme is not one this app can draw.</exception>
    public async Task<GalleryTheme> LoadThemeAsync(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!_themes.TryGetValue(entry.Id, out var attempt))
            _themes[entry.Id] = attempt = FetchThemeAsync(entry);

        try
        {
            return await attempt.ConfigureAwait(false);
        }
        catch
        {
            if (_themes.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, attempt))
                _themes.Remove(entry.Id);
            throw;
        }
    }

    /// <summary>Where a theme's lighting is fetched from.</summary>
    /// <remarks>
    /// Worked out from the id rather than read out of the catalogue, which names no path on purpose.
    /// The id has already been held to <see cref="ValidId"/> — lower case, digits and single dashes —
    /// so there is nothing in it that could point this somewhere else.
    /// </remarks>
    public static string ThemeUrl(string id) => $"{RawRoot}/themes/{id}.json";

    private static void Forget<T>(ref Task<T>? field, Task<T> attempt)
    {
        if (ReferenceEquals(field, attempt)) field = null;
    }

    private async Task<IReadOnlyList<GalleryEntry>> FetchIndexAsync()
    {
        var json = await GetBoundedAsync(IndexFetchUrl(), MaxIndexBytes, "theme catalogue").ConfigureAwait(false);
        var entries = ReadIndex(json, _log);
        if (entries.Count == 0)
            _log.LogWarning("The theme catalogue came back with no themes in it.");
        return entries;
    }

    private async Task<GalleryTheme> FetchThemeAsync(GalleryEntry entry)
    {
        var json = await GetBoundedAsync(ThemeUrl(entry.Id), MaxThemeBytes, $"theme \"{entry.Id}\"")
            .ConfigureAwait(false);
        return new GalleryTheme(entry, ReadThemeFile(json, entry, _log));
    }

    /// <summary>The body of <paramref name="url"/>, or a throw as soon as it is over the limit.</summary>
    /// <remarks>
    /// The size is asked before reading, so an oversized file costs a header rather than a download.
    /// That is not relied on: Content-Length is the server's claim, and the counted read below is
    /// what makes the limit true. Read in chunks and counted rather than read whole and checked
    /// after — the point of a limit is to not have the bytes, and a check after the read has already
    /// had them.
    /// </remarks>
    private async Task<string> GetBoundedAsync(string url, int limit, string what)
    {
        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > limit)
            throw new InvalidDataException($"The {what} says it is {declared} bytes, over the {limit} limit.");

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];

        int read;
        while ((read = await stream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new InvalidDataException($"The {what} is over the {limit} byte limit.");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ── Reading a catalogue nobody here wrote ────────────────────────────────

    /// <summary>
    /// The themes an <c>index.json</c> lists, with everything this app would not draw left out.
    /// </summary>
    /// <param name="json">The catalogue, as fetched.</param>
    /// <param name="log">Told about each theme dropped and why. Optional, for tests.</param>
    /// <remarks>
    /// <para>
    /// One bad entry drops itself and no more than itself. A catalogue is other people's work
    /// arriving in one file, and one malformed entry taking the other two hundred with it would be
    /// the wrong trade — so each is read on its own and the ones that survive are the gallery.
    /// </para>
    /// <para>
    /// What is checked is what the app would otherwise be made to do: names and credits are
    /// length-capped and refused control characters because they are drawn on a card, the id is held
    /// to the pattern the themes repository makes ids with because it is about to become a URL, and
    /// the whole thing is capped because the list is held in memory and filtered on every keystroke.
    /// </para>
    /// <para>
    /// Not checked: whether the theme is any good, and whether the name is one anybody wants to
    /// read. Neither is a thing code decides. See the themes repository's README.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GalleryEntry> ReadIndex(string json, ILogger? log = null)
    {
        IndexFile? index;
        try
        {
            index = JsonSerializer.Deserialize<IndexFile>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The theme catalogue isn't readable JSON.", ex);
        }

        if (index is null) throw new InvalidDataException("The theme catalogue is empty.");
        if (index.Version != IndexVersion)
            throw new InvalidDataException($"The theme catalogue is version {index.Version}, and this app reads version {IndexVersion}.");
        if (index.Themes is null) throw new InvalidDataException("The theme catalogue has no themes list in it.");

        var entries = new List<GalleryEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in index.Themes)
        {
            if (entries.Count >= MaxThemes)
            {
                log?.LogWarning("The theme catalogue has more than {Max} themes in it; the rest are not shown.", MaxThemes);
                break;
            }

            var entry = ReadEntry(element, ids, log);
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>One catalogue entry, or null with a reason logged.</summary>
    private static GalleryEntry? ReadEntry(JsonElement element, HashSet<string> ids, ILogger? log)
    {
        IndexEntry? entry;
        try
        {
            entry = element.Deserialize<IndexEntry>();
        }
        catch (JsonException ex)
        {
            log?.LogWarning(ex, "A theme in the catalogue couldn't be read; skipped.");
            return null;
        }

        if (entry?.Id is null || !ValidId().IsMatch(entry.Id) || entry.Id.Length > MaxIdLength)
        {
            log?.LogWarning("A theme has an id of \"{Id}\", which is not one this gallery can use; skipped.", entry?.Id);
            return null;
        }

        // The id is a theme's identity here — it is what says which card is previewing (see
        // ThemePreview.Active), and it is the file its lighting is fetched from. Two themes
        // answering to one id would make both of those ambiguous.
        if (!ids.Add(entry.Id))
        {
            log?.LogWarning("More than one theme in the catalogue calls itself {Id}; only the first is shown.", entry.Id);
            return null;
        }

        if (!IsDrawableText(entry.Name, MaxThemeNameLength))
        {
            log?.LogWarning("The theme {Id} has a name this gallery won't draw; skipped.", entry.Id);
            return null;
        }

        if (!IsDrawableText(entry.Author, MaxAuthorLength))
        {
            log?.LogWarning("The theme {Id} has a credit this gallery won't draw; skipped.", entry.Id);
            return null;
        }

        // Not required to be present: it decides only whether a theme shows up under "My themes",
        // and a theme with nothing here is simply nobody's, which is the safe way round. An entry
        // is not worth dropping over it — that would be losing a theme everybody can see to a field
        // only its author uses.
        var submittedBy = entry.SubmittedBy ?? "";
        if (submittedBy.Length > 0 && !ValidLogin().IsMatch(submittedBy))
        {
            log?.LogWarning("The theme {Id} says it was submitted by {Who}, which is not a GitHub name; ignored.", entry.Id, submittedBy);
            submittedBy = "";
        }

        return new GalleryEntry(entry.Id, entry.Name!, entry.Author!, submittedBy, entry.Issue);
    }

    /// <summary>Whether <paramref name="text"/> is something worth putting on a card.</summary>
    /// <remarks>
    /// Blazor escapes what it renders, so this is not what stands between a theme name and a script
    /// tag — that is the framework, and it does not need help. This is about the rest: a name of
    /// nothing but combining characters, or one long enough to push a card off the page, or one with
    /// a right-to-left override in it that rearranges the sentence around it.
    /// </remarks>
    private static bool IsDrawableText(string? text, int limit) =>
        !string.IsNullOrWhiteSpace(text)
        && text.Length <= limit
        && !ControlOrFormat().IsMatch(text)
        && HasLetterOrDigit().IsMatch(text);

    // ── Reading one theme's own file ─────────────────────────────────────────

    /// <summary>The lighting out of a <c>themes/&lt;id&gt;.json</c>.</summary>
    /// <param name="json">The file, as fetched.</param>
    /// <param name="entry">The catalogue entry it was fetched for.</param>
    /// <param name="log">Optional, for tests.</param>
    /// <remarks>
    /// A theme file is a stranger's input exactly as the catalogue is, and this is where the checks
    /// that used to run over the inlined lighting now live: brightness in range and keys the SDK
    /// really has, because the alternative is a theme that throws when somebody presses Preview.
    /// Colours need no check — <c>r</c>, <c>g</c> and <c>b</c> land in bytes, and a number that is
    /// not one fails the read rather than this method.
    /// <para>
    /// Unlike a bad catalogue entry, a bad theme file is thrown rather than skipped: it was fetched
    /// because a card is waiting to draw it, and that card wants to say "this didn't load" rather
    /// than quietly show nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidDataException">The file is not a theme this app can draw.</exception>
    public static KeyboardTheme ReadThemeFile(string json, GalleryEntry entry, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ThemeFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ThemeFile>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The theme \"{entry.Id}\" isn't readable JSON.", ex);
        }

        if (file?.Theme is null)
            throw new InvalidDataException($"The file for the theme \"{entry.Id}\" has no lighting in it.");

        // The file is fetched by id, so one that calls itself something else is a repository that
        // disagrees with its own catalogue. Cheap to notice, and the alternative is drawing a theme
        // under a name that belongs to a different one.
        if (!string.Equals(file.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The theme fetched for \"{entry.Id}\" says its id is \"{file.Id}\".");

        if (!IsApplicable(file.Theme, entry.Id, log))
            throw new InvalidDataException($"The theme \"{entry.Id}\" isn't one this app can put on a keyboard.");

        return file.Theme;
    }

    /// <summary>Whether <paramref name="theme"/> is one the keyboard could actually be given.</summary>
    private static bool IsApplicable(KeyboardTheme theme, string id, ILogger? log)
    {
        if (theme.Brightness > MaxBrightness)
        {
            log?.LogWarning("The theme {Id} asks for brightness {Level}; not drawn.", id, theme.Brightness);
            return false;
        }

        if (theme.BaseBrightness > MaxBrightness)
        {
            log?.LogWarning("The theme {Id} asks for base brightness {Level}; not drawn.", id, theme.BaseBrightness);
            return false;
        }

        if (theme.Keys is null) return true;

        if (theme.Keys.Count > MaxKeysPerTheme)
        {
            log?.LogWarning("The theme {Id} sets {Count} keys; not drawn.", id, theme.Keys.Count);
            return false;
        }

        foreach (var name in theme.Keys.Keys)
        {
            // Matched against the names rather than Enum.TryParse, which also accepts a number and
            // would read "5" as whichever key happens to be fifth.
            if (!KeyNames.Contains(name))
            {
                log?.LogWarning("The theme {Id} sets a key called {Key}, which this SDK doesn't have; not drawn.", id, name);
                return false;
            }
        }

        return true;
    }

    /// <summary>Every <see cref="DDKey"/> name, which is what a theme's keys are spelled with.</summary>
    private static readonly HashSet<string> KeyNames = new(Enum.GetNames<DDKey>(), StringComparer.OrdinalIgnoreCase);

    // Mirrors toId() in the themes repository: lower case, digits and single dashes between them.
    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidId();

    /// <summary>GitHub's own rule for an account name: what <c>submittedBy</c> has to look like.</summary>
    /// <remarks>
    /// Letters, digits and single hyphens, up to 39 characters, never starting or ending with one.
    /// Checked because it is compared against a name the user typed on the Settings page, and a
    /// catalogue that could put anything here could put something that matches a name nobody could
    /// type.
    /// </remarks>
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}$")]
    private static partial Regex ValidLogin();

    [GeneratedRegex(@"[\p{Cc}\p{Cf}]")]
    private static partial Regex ControlOrFormat();

    [GeneratedRegex(@"[\p{L}\p{N}]")]
    private static partial Regex HasLetterOrDigit();

    /// <summary>The catalogue file's shape.</summary>
    /// <remarks>
    /// The entries are held as raw elements rather than parsed with the file, so that one that will
    /// not parse is one theme lost and not the catalogue.
    /// </remarks>
    private sealed class IndexFile
    {
        [JsonPropertyName("version")] public int Version { get; set; }
        [JsonPropertyName("themes")] public List<JsonElement>? Themes { get; set; }
    }

    private sealed class IndexEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("submittedBy")] public string? SubmittedBy { get; set; }
        [JsonPropertyName("issue")] public int Issue { get; set; }
    }

    /// <summary>A theme file's shape. Only two of its members are this app's business.</summary>
    private sealed class ThemeFile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("theme")] public KeyboardTheme? Theme { get; set; }
    }

    // ── Copying a theme into the user's profiles ─────────────────────────────

    /// <summary>
    /// Saves <paramref name="theme"/> as a profile of the user's own, named after the theme, and
    /// returns the name it ended up with.
    /// </summary>
    /// <remarks>
    /// The name is not simply the theme's: gallery names are free text and profile names are a
    /// narrow set of characters (see <see cref="ProfileLibrary.IsValidName"/>), so it is translated
    /// and then made unique against what's already saved. Copying the same theme twice is a normal
    /// thing to do — a copy is a starting point people then edit — so the second copy is numbered
    /// rather than silently overwriting the first.
    /// </remarks>
    public async Task<string> CopyToProfileAsync(GalleryTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var existing = await _profiles.ListAsync().ConfigureAwait(false);
        var name = UniqueName(ToProfileName(theme.Name), existing);
        // Theme only: the gallery is about lighting, and a copy that also carried actuation depths
        // would quietly reconfigure how the user's keyboard types.
        await _profiles.SaveAsync(name, new KeyboardProfile { Theme = theme.Theme }).ConfigureAwait(false);
        return name;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex Unusable();

    /// <summary>Turns a theme's free-text name into something <see cref="ProfileLibrary"/> will accept.</summary>
    /// <remarks>
    /// Profile names are letters, digits, '-' and '_' only, because the <c>deerkb</c> CLI turns one
    /// into a file path. So "Ocean Sunrise" is saved as "Ocean_Sunrise" — the gallery keeps the
    /// name the author gave it, and only the copy is renamed.
    /// </remarks>
    public static string ToProfileName(string themeName)
    {
        var cleaned = Unusable().Replace(themeName ?? "", "_").Trim('_');
        if (cleaned.Length > MaxNameLength) cleaned = cleaned[..MaxNameLength];
        // A name of nothing but punctuation cleans down to nothing, and a profile has to be called
        // something. Rare enough to not be worth a cleverer answer.
        return cleaned.Length == 0 ? "Theme" : cleaned;
    }

    /// <summary>
    /// <paramref name="desired"/> if no profile is called that, else it with the lowest free
    /// number on the end.
    /// </summary>
    /// <remarks>
    /// Compared case-insensitively, matching how <see cref="ProfileLibrary.ListAsync"/> orders
    /// names — and because these become file names in the CLI, where on Windows "ember" and "Ember"
    /// are the same profile.
    /// </remarks>
    public static string UniqueName(string desired, IReadOnlyCollection<string> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(desired)) return desired;

        for (int n = 1; ; n++)
        {
            var suffix = $"-{n}";
            // The suffix has to fit inside the 64-character limit rather than push the name past
            // it, or the copy would be rejected by the very rule this method exists to satisfy.
            var stem = desired.Length + suffix.Length > MaxNameLength
                ? desired[..(MaxNameLength - suffix.Length)]
                : desired;
            var candidate = stem + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }

    // Mirrors the length ProfileLibrary's name pattern allows.
    private const int MaxNameLength = 64;
}
