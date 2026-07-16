namespace DrunkDeer.Web.Services;

/// <summary>
/// One theme as the catalogue lists it: what it is called and who by, but not what it looks like.
/// </summary>
/// <param name="Id">Stable identity, unique within the gallery, and the name of the file its lighting is in.</param>
/// <param name="Name">What the theme is called, as its author wrote it. Free text.</param>
/// <param name="Author">Who to credit. Free text, and whatever the submitter asked for.</param>
/// <param name="SubmittedBy">The GitHub account that published it.</param>
/// <param name="Issue">The issue it was published from. 0 for the themes seeded with the repository.</param>
/// <remarks>
/// <para>
/// This is everything <c>index.json</c> carries. The lighting lives in <c>themes/&lt;id&gt;.json</c>
/// and is fetched per theme, when there is a card to draw with it — see
/// <see cref="ThemeGallery.LoadThemeAsync"/>. A gallery that pages through hundreds of themes then
/// costs a page of themes rather than all of them, and that split is the whole reason this type is
/// separate from <see cref="GalleryTheme"/>.
/// </para>
/// <para>
/// <see cref="Author"/> and <see cref="SubmittedBy"/> are not the same thing and must not be used
/// as though they were. The first is a credit: free text, unverified, and what a card shows. The
/// second is evidence — the account GitHub says opened the issue — and it is what "is this one of
/// mine" is answered with. Neither is a secret; both are already public on the issue.
/// </para>
/// </remarks>
public sealed record GalleryEntry(string Id, string Name, string Author, string SubmittedBy, int Issue);
