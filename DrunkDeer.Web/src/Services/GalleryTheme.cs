using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Services;

/// <summary>One gallery theme with its lighting in hand: the catalogue entry, and the picture.</summary>
/// <param name="Entry">What the catalogue says about it.</param>
/// <param name="Theme">The lighting itself — the same <see cref="KeyboardTheme"/> a profile carries.</param>
/// <remarks>
/// Only exists once the theme's own file has been fetched and read, which is why it is a separate
/// type from <see cref="GalleryEntry"/> rather than a nullable field on it: a card that has one of
/// these can draw, copy and preview, and a card that has only an entry cannot. The compiler keeps
/// that straight so the UI does not have to.
/// <para>
/// <see cref="Name"/> is free text and <see cref="ProfileLibrary"/> names are not, so copying one
/// into the other is a translation rather than an assignment — see
/// <see cref="ThemeGallery.CopyToProfileAsync"/>.
/// </para>
/// </remarks>
public sealed record GalleryTheme(GalleryEntry Entry, KeyboardTheme Theme)
{
    /// <inheritdoc cref="GalleryEntry.Id"/>
    public string Id => Entry.Id;

    /// <inheritdoc cref="GalleryEntry.Name"/>
    public string Name => Entry.Name;

    /// <inheritdoc cref="GalleryEntry.Author"/>
    public string Author => Entry.Author;
}
