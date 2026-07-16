using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Narrowing the gallery down and cutting it into pages: the search box, the author link, the
/// "My themes" filter and the pager, without a browser to click them in.
/// </summary>
[TestFixture]
public class GalleryFilterTests
{
	private static GalleryEntry Entry(string id, string name, string author = "DrunkDeer", string submittedBy = "deerios") =>
		new(id, name, author, submittedBy, 1);

	private static readonly IReadOnlyList<GalleryEntry> Themes =
	[
		Entry("ember", "Ember", "DrunkDeer", "deerios"),
		Entry("ocean-sunrise", "Ocean Sunrise", "Ada", "ada-l"),
		Entry("deep-ocean", "Deep Ocean", "Grace", "gracehop"),
		Entry("nord", "Nord", "Ada", "ada-l"),
	];

	private static IEnumerable<string> Ids(IReadOnlyList<GalleryEntry> entries) => entries.Select(e => e.Id);

	// ── Searching ────────────────────────────────────────────────────────────

	[Test]
	public void NoSearch_IsEverything()
	{
		Assert.That(GalleryFilter.Matching(Themes), Has.Count.EqualTo(4));
	}

	[TestCase("")]
	[TestCase("   ")]
	[TestCase(null)]
	public void AnEmptySearch_IsEverything(string? query)
	{
		Assert.That(GalleryFilter.Matching(Themes, query), Has.Count.EqualTo(4));
	}

	[Test]
	public void ASearch_MatchesPartOfAName()
	{
		Assert.That(Ids(GalleryFilter.Matching(Themes, "ocean")), Is.EqualTo(new[] { "ocean-sunrise", "deep-ocean" }));
	}

	[Test]
	public void ASearch_IgnoresCase()
	{
		Assert.That(Ids(GalleryFilter.Matching(Themes, "EMBER")), Is.EqualTo(new[] { "ember" }));
	}

	[Test]
	public void ASearch_MatchesTheCreditToo()
	{
		// What a search box is expected to do, and the reason the author filter is worth having: you
		// cannot click an author you cannot find.
		Assert.That(Ids(GalleryFilter.Matching(Themes, "ada")), Is.EqualTo(new[] { "ocean-sunrise", "nord" }));
	}

	[Test]
	public void ASearch_IsTrimmed()
	{
		Assert.That(Ids(GalleryFilter.Matching(Themes, "  ember  ")), Is.EqualTo(new[] { "ember" }));
	}

	[Test]
	public void ASearchMatchingNothing_IsEmpty()
	{
		Assert.That(GalleryFilter.Matching(Themes, "sausage"), Is.Empty);
	}

	[Test]
	public void ASearch_KeepsTheCataloguesOrder()
	{
		// Not ranked: there is nothing here worth ranking, and reordering the page under somebody as
		// they type is its own bug.
		Assert.That(Ids(GalleryFilter.Matching(Themes, "o")), Is.EqualTo(new[] { "ocean-sunrise", "deep-ocean", "nord" }));
	}

	// ── By author ────────────────────────────────────────────────────────────

	[Test]
	public void AnAuthor_IsExact_NotASearch()
	{
		// "Ada" must not also bring back a theme by "Adam". The filter means this author.
		var withAdam = Themes.Append(Entry("x", "X", "Adam", "adam")).ToList();

		Assert.That(Ids(GalleryFilter.Matching(withAdam, author: "Ada")), Is.EqualTo(new[] { "ocean-sunrise", "nord" }));
	}

	[Test]
	public void AnAuthor_IgnoresCase()
	{
		Assert.That(GalleryFilter.Matching(Themes, author: "ada"), Has.Count.EqualTo(2));
	}

	[Test]
	public void AnAuthorAndASearch_BothApply()
	{
		Assert.That(Ids(GalleryFilter.Matching(Themes, query: "ocean", author: "Ada")), Is.EqualTo(new[] { "ocean-sunrise" }));
	}

	// ── Mine ─────────────────────────────────────────────────────────────────

	[Test]
	public void MyThemes_AreTheOnesMyAccountPublished()
	{
		Assert.That(Ids(GalleryFilter.Matching(Themes, submittedBy: "ada-l")), Is.EqualTo(new[] { "ocean-sunrise", "nord" }));
	}

	[Test]
	public void MyThemes_GoByTheAccount_NotTheCredit()
	{
		// The credit is free text anybody can type; the account is what GitHub said. A theme
		// credited to "Ada" that somebody else published is not Ada's to modify, and the themes
		// repository would refuse it — so the gallery must not offer it.
		//
		// The credit here is deliberately the login being searched for, not a name that merely looks
		// like Ada's. With a plausible-looking credit the test passed just as well against a filter
		// that read the credit instead of the account — the two strings never met, so it proved
		// nothing. Anybody can type "ada-l" into the credit box; that is the whole point.
		var impostor = new[] { Entry("fake", "Fake", author: "ada-l", submittedBy: "somebody-else") };

		Assert.That(GalleryFilter.Matching(impostor, submittedBy: "ada-l"), Is.Empty);
	}

	[Test]
	public void MyThemes_IgnoresTheCaseOfALogin()
	{
		Assert.That(GalleryFilter.Matching(Themes, submittedBy: "Ada-L"), Has.Count.EqualTo(2));
	}

	[Test]
	public void AThemeNobodyPublished_IsNobodys()
	{
		// An entry with no submittedBy: it must not fall to whoever happens to be asking.
		var orphan = new[] { new GalleryEntry("x", "X", "DrunkDeer", "", 0) };

		Assert.That(GalleryFilter.Matching(orphan, submittedBy: "deerios"), Is.Empty);
	}

	[Test]
	public void NoAccount_IsEverybodys()
	{
		// Null means the filter is off, not "themes belonging to nobody".
		Assert.That(GalleryFilter.Matching(Themes, submittedBy: null), Has.Count.EqualTo(4));
	}

	// ── Paging ───────────────────────────────────────────────────────────────

	private static readonly IReadOnlyList<GalleryEntry> Many =
		Enumerable.Range(1, 30).Select(n => Entry($"t{n:00}", $"Theme {n}")).ToList();

	[Test]
	public void APage_IsTwelveThemes()
	{
		Assert.That(GalleryFilter.Page(Many, 1), Has.Count.EqualTo(GalleryFilter.PageSize));
		Assert.That(GalleryFilter.Page(Many, 1)[0].Id, Is.EqualTo("t01"));
	}

	[Test]
	public void TheSecondPage_CarriesOnWhereTheFirstStopped()
	{
		Assert.That(GalleryFilter.Page(Many, 2)[0].Id, Is.EqualTo("t13"));
	}

	[Test]
	public void TheLastPage_IsWhateverIsLeft()
	{
		Assert.That(GalleryFilter.Page(Many, 3), Has.Count.EqualTo(6));
	}

	[Test]
	public void EveryThemeIsOnExactlyOnePage()
	{
		var paged = Enumerable.Range(1, GalleryFilter.PageCount(Many.Count))
			.SelectMany(p => GalleryFilter.Page(Many, p))
			.ToList();

		Assert.That(Ids(paged), Is.EqualTo(Ids(Many)));
	}

	[Test]
	public void APagePastTheEnd_IsEmptyRatherThanAThrow()
	{
		// The page number arrives in the URL, where anybody can type one — and a deep link to page 9
		// goes stale on its own when themes are unpublished.
		Assert.That(GalleryFilter.Page(Many, 99), Is.Empty);
	}

	[TestCase(0)]
	[TestCase(-1)]
	public void APageBeforeTheStart_IsTheFirstPage(int page)
	{
		Assert.That(GalleryFilter.Page(Many, page)[0].Id, Is.EqualTo("t01"));
	}

	[Test]
	public void ThePageCount_CoversEverything()
	{
		Assert.That(GalleryFilter.PageCount(30), Is.EqualTo(3));
		Assert.That(GalleryFilter.PageCount(24), Is.EqualTo(2), "an exact fit is not a spare empty page");
		Assert.That(GalleryFilter.PageCount(25), Is.EqualTo(3));
	}

	[Test]
	public void AnEmptyGallery_IsOnePage()
	{
		// Zero pages is a state the pager cannot draw, and "no themes match" is a thing to say on a
		// page rather than instead of one.
		Assert.That(GalleryFilter.PageCount(0), Is.EqualTo(1));
	}
}
