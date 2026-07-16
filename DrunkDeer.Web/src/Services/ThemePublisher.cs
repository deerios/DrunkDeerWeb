using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Publishing a theme to the shared gallery, which is a GitHub repository that takes submissions as
/// issues: this builds the link that opens one with the theme already in it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here talks to GitHub. The app has no account to talk to it with, and asking for one to
/// share a lighting theme would be a strange trade — so the user's own browser and their own GitHub
/// session do the submitting, and the app's part is to fill in the form. That is also why this is
/// all static and testable: the whole feature is a string.
/// </para>
/// <para>
/// What happens to the issue afterwards — checking it, opening a pull request, merging it — belongs
/// to the themes repository, in <c>.github/workflows/new-theme.yml</c>. The two agree on the field
/// names in <see cref="Field"/> and on the shape of the theme JSON, and nothing enforces that
/// agreement from here.
/// </para>
/// </remarks>
public static class ThemePublisher
{
    /// <summary>The repository themes are published to.</summary>
    public const string Repository = "deerios/DrunkDeerThemes";

    /// <summary>Where a person can read what happens to a theme after they submit it.</summary>
    public const string RepositoryUrl = $"https://github.com/{Repository}";

    private const string NewIssueUrl = $"{RepositoryUrl}/issues/new";

    /// <summary>The issue forms this fills in, by file name.</summary>
    /// <remarks>
    /// One per thing a person can ask for. They are separate forms rather than one with a "what do
    /// you want" box because the label on each is what the themes repository routes on — see the
    /// workflows there — and because a form that asks for a theme's lighting is the wrong thing to
    /// put in front of somebody who wants it deleted.
    /// </remarks>
    private static class Template
    {
        public const string Publish = "new-theme.yml";
        public const string Update = "update-theme.yml";
        public const string Remove = "remove-theme.yml";
    }

    /// <summary>
    /// The forms' field ids. A field is prefilled by a query parameter named after its id, so these
    /// must match the <c>id:</c>s in the themes repository's <c>.github/ISSUE_TEMPLATE/*.yml</c>
    /// — a wrong one is ignored in silence and the field simply comes up empty.
    /// </summary>
    private static class Field
    {
        public const string Name = "theme-name";
        public const string Author = "credit";
        public const string Json = "theme-json";
        public const string Id = "theme-id";
    }

    /// <summary>
    /// The longest URL this will hand to the browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prefilled form is a URL with the whole theme escaped into it, and past GitHub's limit the
    /// answer is an error page rather than the form: the theme would be gone and the user would be
    /// looking at a 500. <see cref="Fits"/> is how the caller finds out before that happens.
    /// </para>
    /// <para>
    /// Measured against github.com on 2026-07-15 rather than guessed, because the documentation
    /// gives no number: a query of 6800 characters was served every time, 6900 failed about half
    /// the time, 7000 failed every time with a 500, 8000 dropped the connection, and only past 8200
    /// did it become the documented "414 URI Too Long". So the ceiling is roughly 6900 and the
    /// failure either side of it is not the polite one — this stops short of the part that works.
    /// </para>
    /// <para>
    /// It is the ceiling for any GitHub URL this app hands to a browser, not just <c>/issues/new</c>:
    /// signed out, that URL is not the one the browser ends up on — see <see cref="Fits"/>.
    /// </para>
    /// </remarks>
    public const int MaxUrlLength = 6800;

    /// <summary>
    /// What a signed-out click is 302'd to before <see cref="NewIssueUrl"/> is ever reached.
    /// </summary>
    private const string SignedOutRedirect = "https://github.com/login?return_to=";

    // Compact, unlike the SDK's own KeyboardProfile.ToJson, which indents: this goes in a URL, where
    // every space costs three characters once escaped. The fenced block in the issue is readable
    // either way. Reflection-serialised like the rest of the profile JSON in this app — see
    // TrimmerRoots.xml for why that survives publishing.
    private static readonly JsonSerializerOptions _compact = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>The theme as the JSON the submission carries.</summary>
    public static string ToJson(KeyboardTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return JsonSerializer.Serialize(theme, _compact);
    }

    /// <summary>
    /// The theme as the packed form the URL carries: <c>z1.</c> followed by the base64url of the
    /// theme JSON, raw-deflated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The theme JSON is the same handful of properties repeated once per key —
    /// <c>{"key":"W","r":255,...}</c> eighty-some times over — which deflates hard, and that is the
    /// whole reason this exists: <see cref="ToJson"/> alone is too long to survive being escaped
    /// twice into a signed-out GitHub URL (see <see cref="Fits"/>).
    /// </para>
    /// <para>
    /// Raw deflate, not zlib or gzip, because that is what the themes repository's Node reads it
    /// back with (<c>zlib.inflateRawSync</c>) — the two have to agree on the exact framing or every
    /// packed theme fails to decode with no way to tell why. Base64url rather than plain base64
    /// because its whole alphabet, plus the <c>.</c> that separates the prefix, is unreserved in
    /// RFC 3986: <see cref="Uri.EscapeDataString"/> leaves it untouched, which is what stops the
    /// login-redirect round trip from charging the escaping twice the way it does for JSON.
    /// </para>
    /// <para>
    /// <c>z1</c> names the format and its version, so a later change to the packing is a new prefix
    /// the themes repository can recognise and reject with a clear message, rather than a guess at
    /// the receiving end about what changed.
    /// </para>
    /// </remarks>
    public static string ToPacked(KeyboardTheme theme)
    {
        var json = Encoding.UTF8.GetBytes(ToJson(theme));

        using var deflated = new MemoryStream();
        using (var deflate = new DeflateStream(deflated, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(json);

        var base64 = Convert.ToBase64String(deflated.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"z1.{base64}";
    }

    /// <summary>
    /// The link that opens a new issue with <paramref name="theme"/> already filled in.
    /// </summary>
    /// <param name="name">What the theme should be called in the gallery.</param>
    /// <param name="author">Who to credit. Empty means the submitter's GitHub username.</param>
    /// <param name="theme">The lighting itself.</param>
    /// <remarks>
    /// The confirmation checkbox is deliberately not filled in — GitHub cannot prefill one, and it
    /// should not be possible anyway: it is the one part of the form that has to be the submitter's
    /// own answer rather than this app's.
    /// </remarks>
    public static string BuildIssueUrl(string name, string author, KeyboardTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var query = new List<string>
        {
            $"template={Uri.EscapeDataString(Template.Publish)}",
            // The form's own title is a bare "Theme: " prefix for people who found the form on their
            // own. Coming from the app the name is already known, so it finishes the sentence.
            $"title={Uri.EscapeDataString($"Theme: {name?.Trim()}")}",
            $"{Field.Name}={Uri.EscapeDataString(name?.Trim() ?? "")}",
            $"{Field.Json}={Uri.EscapeDataString(ToPacked(theme))}",
        };

        // Left out entirely rather than sent empty: the form says an empty credit means "use my
        // GitHub username", and a parameter present but blank says the same thing less clearly.
        if (!string.IsNullOrWhiteSpace(author))
            query.Add($"{Field.Author}={Uri.EscapeDataString(author.Trim())}");

        return $"{NewIssueUrl}?{string.Join('&', query)}";
    }

    /// <summary>
    /// The link that opens an issue asking for <paramref name="id"/>'s lighting to be replaced with
    /// <paramref name="theme"/>.
    /// </summary>
    /// <param name="id">The theme to update, as the catalogue lists it.</param>
    /// <param name="name">What it is called, for the issue's title. Not changed by the update.</param>
    /// <param name="theme">The lighting to replace it with.</param>
    /// <remarks>
    /// <para>
    /// Carries no credit and no name to change: an update replaces the picture and nothing else, and
    /// the id is what says which theme is meant. Renaming a theme means unpublishing and publishing
    /// it again, because the name is where the id comes from and the id is the file.
    /// </para>
    /// <para>
    /// Nothing here says who is asking, and nothing here could: whether this is allowed rests on the
    /// GitHub account that submits the issue, which is checked at the other end against the account
    /// that published the theme. This link is an offer to fill in a form — the app cannot grant
    /// itself permission by putting a name in a URL, and does not try to.
    /// </para>
    /// </remarks>
    public static string BuildUpdateIssueUrl(string id, string name, KeyboardTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return BuildUpdateIssueUrl(id, name, ToPacked(theme));
    }

    /// <summary>The same form with the theme left out, for the clipboard fallback.</summary>
    /// <remarks>
    /// The id still travels: it is a few characters, it is not what makes the link too long, and
    /// asking somebody to paste the theme is bad enough without also asking them to know its id.
    /// </remarks>
    public static string BuildEmptyUpdateIssueUrl(string id, string name) => BuildUpdateIssueUrl(id, name, packed: null);

    private static string BuildUpdateIssueUrl(string id, string name, string? packed)
    {
        var query = new List<string>
        {
            $"template={Uri.EscapeDataString(Template.Update)}",
            $"title={Uri.EscapeDataString($"Update theme: {name?.Trim()}")}",
            $"{Field.Id}={Uri.EscapeDataString(id?.Trim() ?? "")}",
        };

        if (packed is not null)
            query.Add($"{Field.Json}={Uri.EscapeDataString(packed)}");

        return $"{NewIssueUrl}?{string.Join('&', query)}";
    }

    /// <summary>The link that opens an issue asking for <paramref name="id"/> to be taken out of the gallery.</summary>
    /// <remarks>
    /// Carries no theme, so unlike the other two it is short whatever the theme is and
    /// <see cref="Fits"/> can never turn it away. Same rule about who may: it is the submitting
    /// account that decides, not this URL.
    /// </remarks>
    public static string BuildRemoveIssueUrl(string id, string name)
    {
        var query = new List<string>
        {
            $"template={Uri.EscapeDataString(Template.Remove)}",
            $"title={Uri.EscapeDataString($"Remove theme: {name?.Trim()}")}",
            $"{Field.Id}={Uri.EscapeDataString(id?.Trim() ?? "")}",
        };

        return $"{NewIssueUrl}?{string.Join('&', query)}";
    }

    /// <summary>Whether <paramref name="url"/> is one the browser and GitHub will both accept.</summary>
    /// <remarks>
    /// Not <paramref name="url"/>'s own length: a submitter who is not signed in to GitHub never
    /// requests <paramref name="url"/> at all. <c>/issues/new</c> answers a signed-out request with a
    /// 302 to <c>/login?return_to=&lt;url, escaped again&gt;</c>, so the escaping is charged twice and
    /// it is the login URL — longer than <paramref name="url"/> by construction — that has to survive
    /// GitHub's limit. Checking <paramref name="url"/> itself would pass a theme that 502s for anyone
    /// who has not already signed in, which was this method's whole bug: it said a link fit while it
    /// was breaking for exactly the submitters who most needed it to work.
    /// </remarks>
    public static bool Fits(string url) =>
        SignedOutRedirect.Length + Uri.EscapeDataString(url ?? "").Length <= MaxUrlLength;

    /// <summary>The link that opens the same form with nothing filled in.</summary>
    /// <remarks>
    /// For the theme too big to travel in a URL: the user is handed the JSON on the clipboard and
    /// the empty form to paste it into. Rare — it takes a theme several times larger than a full
    /// keyboard's worth of colours — but the alternative is a "414 URI Too Long" page.
    /// </remarks>
    public static string BuildEmptyIssueUrl() => $"{NewIssueUrl}?template={Uri.EscapeDataString(Template.Publish)}";
}
