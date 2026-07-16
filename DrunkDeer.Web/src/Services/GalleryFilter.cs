namespace DrunkDeer.Web.Services;

/// <summary>
/// Narrowing the gallery down to what somebody asked for, and cutting the result into pages.
/// </summary>
/// <remarks>
/// Pure and static, over the catalogue the page already has in memory: an entry is a hundred bytes
/// and there are at most <see cref="ThemeGallery.MaxThemes"/> of them, so searching them is a loop
/// over a list and not something worth a round trip. Kept out of the page so that what it does can
/// be tested without a browser to type into.
/// </remarks>
public static class GalleryFilter
{
    /// <summary>How many themes a page shows.</summary>
    /// <remarks>
    /// Twelve because it divides by the 1, 2 and 3 columns the cards lay out in, so no page ends in
    /// a ragged row. It is also how many theme files a page turn fetches — see
    /// <see cref="ThemeGallery.LoadThemeAsync"/> — so it is not a number to raise idly.
    /// </remarks>
    public const int PageSize = 12;

    /// <summary>
    /// The themes matching a search, an author, and whose they are.
    /// </summary>
    /// <param name="entries">The whole catalogue.</param>
    /// <param name="query">Free text. Matched against the name and the credit. Null or blank matches everything.</param>
    /// <param name="author">Exact credit to show, as clicked on a card. Null shows every author.</param>
    /// <param name="submittedBy">Only themes this account published. Null shows everybody's.</param>
    /// <remarks>
    /// <para>
    /// The order is the catalogue's, which is by id — stable, and not something a search should
    /// shuffle. Ranking by how well a theme matches would reorder the page under someone as they
    /// typed, and there is nothing here worth ranking: the whole corpus is two short strings.
    /// </para>
    /// <para>
    /// The search matches the credit as well as the name because that is what people expect of a
    /// search box, and because the author filter is a click on a card — you cannot click a name you
    /// cannot find. They are separate arguments rather than one because they answer differently:
    /// typing "deer" finds anything mentioning it, while the filter means this author exactly.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<GalleryEntry> Matching(
        IReadOnlyList<GalleryEntry> entries,
        string? query = null,
        string? author = null,
        string? submittedBy = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        IEnumerable<GalleryEntry> matching = entries;

        if (!string.IsNullOrWhiteSpace(author))
        {
            var wanted = author.Trim();
            matching = matching.Where(e => string.Equals(e.Author, wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(submittedBy))
        {
            // The account, not the credit: a credit is free text anybody can type, and "these are
            // mine" has to rest on the thing GitHub said. Compared without case because logins are.
            var who = submittedBy.Trim();
            matching = matching.Where(e => string.Equals(e.SubmittedBy, who, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var text = query.Trim();
            matching = matching.Where(e => Contains(e.Name, text) || Contains(e.Author, text));
        }

        return matching.ToList();
    }

    /// <summary>The <paramref name="page"/>th page of <paramref name="entries"/>, counting from 1.</summary>
    /// <remarks>
    /// A page past the end comes back empty rather than throwing: the page number arrives in the
    /// URL, where anybody can type one, and a theme unpublished under a deep-linked page 9 makes an
    /// honest one go out of range on its own. See <see cref="PageCount"/> for the number to clamp to.
    /// </remarks>
    public static IReadOnlyList<GalleryEntry> Page(IReadOnlyList<GalleryEntry> entries, int page)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (page < 1) page = 1;
        return entries.Skip((page - 1) * PageSize).Take(PageSize).ToList();
    }

    /// <summary>How many pages <paramref name="count"/> themes make. Always at least one.</summary>
    /// <remarks>
    /// An empty gallery is one empty page rather than none: zero pages is a state the pager cannot
    /// draw, and "no themes match" is a thing to say on a page rather than instead of one.
    /// </remarks>
    public static int PageCount(int count) => Math.Max(1, (count + PageSize - 1) / PageSize);

    private static bool Contains(string? text, string query) =>
        text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
}
