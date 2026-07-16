using System.Net;
using System.Text;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Fetching the shared catalogue and the themes in it: the size limits, the failures, and the
/// promise that a page of cards costs a page of themes.
/// </summary>
/// <remarks>
/// <see cref="ThemeCatalogueTests"/> covers what a catalogue and a theme file are allowed to
/// contain. This covers getting hold of them.
/// </remarks>
[TestFixture]
public class ThemeCatalogueFetchTests
{
	/// <summary>An HttpClient answering each request from whatever the test put in the table.</summary>
	/// <remarks>
	/// Records what was asked for, in order, because "which themes did the page fetch" is the thing
	/// half these tests are about — an assertion on the answer would pass just as well if the app
	/// had downloaded the whole gallery to show six of it.
	/// </remarks>
	private sealed class Stub : HttpMessageHandler
	{
		private readonly Func<string, HttpResponseMessage> _reply;
		public List<string> Requested { get; } = [];
		public int Calls => Requested.Count;

		/// <summary>What was asked for, with any query string dropped.</summary>
		/// <remarks>
		/// The catalogue is fetched with a cache-buster on the end, which is deliberately different
		/// every time — so "which files did the app ask for" is a question about the part in front of
		/// the '?'. The tests that are about the cache-buster itself read <see cref="Requested"/>.
		/// </remarks>
		public IEnumerable<string> Asked => Requested.Select(WithoutQuery);

		public Stub(Func<string, HttpResponseMessage> reply) => _reply = reply;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			var url = request.RequestUri!.ToString();
			Requested.Add(url);
			return Task.FromResult(_reply(url));
		}
	}

	private static string WithoutQuery(string url) => url.Split('?')[0];

	/// <summary>Whether a request is for the catalogue, whatever cache-buster it carries.</summary>
	private static bool IsIndex(string url) => WithoutQuery(url) == ThemeGallery.IndexUrl;

	private static HttpResponseMessage Ok(string body, long? declaredLength = null)
	{
		var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
		// Set explicitly so a response can lie about its own size.
		content.Headers.ContentLength = declaredLength ?? Encoding.UTF8.GetByteCount(body);
		return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
	}

	private static (ThemeGallery Gallery, Stub Handler) Gallery(Func<string, HttpResponseMessage> reply)
	{
		var handler = new Stub(reply);
		return (new ThemeGallery(null!, new HttpClient(handler), NullLogger<ThemeGallery>.Instance), handler);
	}

	private static string Index(params string[] ids) =>
		$$"""
		{"version": 2, "themes": [{{string.Join(",", ids.Select(id =>
			$$"""{"id": "{{id}}", "name": "{{id}}", "author": "DrunkDeer", "submittedBy": "deerios", "issue": 1}"""))}}]}
		""";

	/// <summary>The lighting every theme in these tests has. R=120 is what the assertions look for.</summary>
	private const string Lighting = """{"brightness": 9, "baseColor": {"r": 120, "g": 20, "b": 0}}""";

	// Concatenated rather than interpolated: the JSON ends in a run of closing braces, and a raw
	// interpolated literal needs one more '$' than the longest run — which is a puzzle to read and
	// re-solve every time the shape changes.
	private static string ThemeFile(string id) =>
		"{\"id\": \"" + id + "\", \"name\": \"" + id + "\", \"author\": \"DrunkDeer\", " +
		"\"submittedBy\": \"deerios\", \"issue\": 1, \"theme\": " + Lighting + "}";

	/// <summary>The whole repository: an index of the given themes, and a file for each.</summary>
	private static Func<string, HttpResponseMessage> Repository(params string[] ids) => url =>
		IsIndex(url)
			? Ok(Index(ids))
			: ids.FirstOrDefault(id => url == ThemeGallery.ThemeUrl(id)) is { } found
				? Ok(ThemeFile(found))
				: new HttpResponseMessage(HttpStatusCode.NotFound);

	// ── The catalogue ────────────────────────────────────────────────────────

	[Test]
	public async Task AGoodCatalogue_IsWhatTheGalleryLists()
	{
		var (gallery, _) = Gallery(Repository("ember"));

		var themes = await gallery.ListAsync();

		Assert.That(themes.Select(t => t.Name), Is.EqualTo(new[] { "ember" }));
	}

	[Test]
	public async Task ItIsFetchedOnce_HoweverManyCardsAsk()
	{
		var (gallery, handler) = Gallery(Repository("ember"));

		await gallery.ListAsync();
		await gallery.ListAsync();
		await gallery.ListAsync();

		Assert.That(handler.Calls, Is.EqualTo(1));
	}

	[Test]
	public async Task ListingTheGallery_FetchesNoThemes()
	{
		// The whole point of the metadata index: opening the gallery costs one file, not one per
		// theme. A card asks for its own when it is drawn.
		var (gallery, handler) = Gallery(Repository("ember", "nord", "matrix"));

		await gallery.ListAsync();

		Assert.That(handler.Asked, Is.EqualTo(new[] { ThemeGallery.IndexUrl }));
	}

	// ── Getting past the caches ──────────────────────────────────────────────

	[Test]
	public async Task TheCatalogue_IsFetchedWithSomethingNoCacheHasSeen()
	{
		var (gallery, handler) = Gallery(Repository("ember"));

		await gallery.ListAsync();

		Assert.That(handler.Requested.Single(), Is.Not.EqualTo(ThemeGallery.IndexUrl));
		Assert.That(handler.Requested.Single(), Does.StartWith(ThemeGallery.IndexUrl + "?t="));
	}

	[Test]
	public async Task Refreshing_AsksForAUrlItHasNeverAskedFor()
	{
		// The regression that matters, and the whole reason the cache-buster exists. GitHub serves
		// the catalogue through a CDN that holds it for minutes and keys on the URL, so a Refresh
		// that asked the same question twice would be answered from the copy taken before the user's
		// theme was merged — however many times they pressed it.
		var (gallery, handler) = Gallery(Repository("ember"));
		await gallery.ListAsync();

		gallery.Reset();
		await gallery.ListAsync();

		Assert.That(handler.Requested, Has.Count.EqualTo(2));
		Assert.That(handler.Requested[1], Is.Not.EqualTo(handler.Requested[0]));
	}

	[Test]
	public async Task ATheme_IsFetchedPlain()
	{
		// A theme file is written once under an id that is never reused, so a cached one is never
		// wrong. Busting these would cost the sharing that makes paging cheap and buy nothing.
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();

		await gallery.LoadThemeAsync(entry);

		Assert.That(handler.Requested, Does.Contain(ThemeGallery.ThemeUrl("ember")));
	}

	// ── When it does not work ────────────────────────────────────────────────

	[Test]
	public void AFetchThatFails_IsSaidRatherThanPapered()
	{
		// There is no offline copy to fall back to, deliberately — see ThemeGallery. A gallery that
		// quietly showed a stale, smaller version of itself would be the worse answer.
		var (gallery, _) = Gallery(_ => throw new HttpRequestException("no network"));

		Assert.ThrowsAsync<HttpRequestException>(() => gallery.ListAsync());
	}

	[Test]
	public void ANotFound_IsSaid()
	{
		var (gallery, _) = Gallery(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

		Assert.ThrowsAsync<HttpRequestException>(() => gallery.ListAsync());
	}

	[Test]
	public void AnUnreadableCatalogue_IsSaid()
	{
		var (gallery, _) = Gallery(_ => Ok("<html>404 not found</html>"));

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.ListAsync());
	}

	[Test]
	public async Task AnEmptyCatalogue_IsAnEmptyGallery_NotAnError()
	{
		// A repository with nothing in it is a fact, not a failure. The page says "no themes yet".
		var (gallery, _) = Gallery(_ => Ok("""{"version": 2, "themes": []}"""));

		Assert.That(await gallery.ListAsync(), Is.Empty);
	}

	[Test]
	public async Task AFailedFetch_IsNotRememberedAsTheAnswer()
	{
		// A tab left open through a tunnel should not be stuck on the error until it is reloaded.
		var fail = true;
		var (gallery, _) = Gallery(url => fail ? throw new HttpRequestException("no network") : Repository("ember")(url));

		Assert.ThrowsAsync<HttpRequestException>(() => gallery.ListAsync());
		fail = false;

		Assert.That((await gallery.ListAsync()).Select(t => t.Id), Is.EqualTo(new[] { "ember" }));
	}

	[Test]
	public async Task AFetchThatFailsWithoutEverAwaiting_IsStillNotRemembered()
	{
		// The trap this shape exists for: an attempt that throws synchronously runs to its end
		// during the `??=`, so clearing the cached task from inside it would happen before the
		// assignment it was meant to undo — and the failure would be the answer for ever.
		var fail = true;
		var (gallery, _) = Gallery(url =>
			fail ? throw new InvalidOperationException("thrown before any await") : Repository("ember")(url));

		Assert.ThrowsAsync<InvalidOperationException>(() => gallery.ListAsync());
		fail = false;

		Assert.That(await gallery.ListAsync(), Has.Count.EqualTo(1));
	}

	// ── One theme at a time ──────────────────────────────────────────────────

	[Test]
	public async Task AThemeIsFetchedFromItsOwnFile()
	{
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();

		var theme = await gallery.LoadThemeAsync(entry);

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
		Assert.That(handler.Requested, Does.Contain(ThemeGallery.ThemeUrl("ember")));
	}

	[Test]
	public async Task ThemesAreFetchedOnlyAsTheyAreAskedFor()
	{
		// What paging rests on: three themes listed, one drawn, one fetched.
		var (gallery, handler) = Gallery(Repository("ember", "nord", "matrix"));
		var entries = await gallery.ListAsync();

		await gallery.LoadThemeAsync(entries.Single(e => e.Id == "nord"));

		Assert.That(handler.Asked, Is.EqualTo(new[] { ThemeGallery.IndexUrl, ThemeGallery.ThemeUrl("nord") }));
	}

	[Test]
	public async Task AThemeIsFetchedOnce_HoweverManyTimesItIsShown()
	{
		// Two cards for one theme share a fetch, and paging back to a theme costs nothing.
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();

		await gallery.LoadThemeAsync(entry);
		await gallery.LoadThemeAsync(entry);

		Assert.That(handler.Requested.Count(u => u == ThemeGallery.ThemeUrl("ember")), Is.EqualTo(1));
	}

	[Test]
	public async Task TwoCardsAskingAtOnce_ShareOneFetch()
	{
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();

		await Task.WhenAll(gallery.LoadThemeAsync(entry), gallery.LoadThemeAsync(entry));

		Assert.That(handler.Requested.Count(u => u == ThemeGallery.ThemeUrl("ember")), Is.EqualTo(1));
	}

	[Test]
	public async Task AThemeThatWillNotLoad_IsOnlyThatThemesProblem()
	{
		// One card says it could not load; the gallery around it is fine. A theme file is other
		// people's work exactly as the catalogue is.
		var (gallery, _) = Gallery(url =>
			IsIndex(url) ? Ok(Index("ember", "nord"))
			: url == ThemeGallery.ThemeUrl("nord") ? Ok("<html>nope</html>")
			: Ok(ThemeFile("ember")));

		var entries = await gallery.ListAsync();

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.LoadThemeAsync(entries.Single(e => e.Id == "nord")));
		Assert.That((await gallery.LoadThemeAsync(entries.Single(e => e.Id == "ember"))).Name, Is.EqualTo("ember"));
	}

	[Test]
	public async Task AThemeThatFailed_CanBeTriedAgain()
	{
		// What the card's "try again" is for. Same rule as the catalogue: a failure is not an answer
		// worth remembering.
		var fail = true;
		var (gallery, _) = Gallery(url =>
			IsIndex(url) ? Ok(Index("ember"))
			: fail ? throw new HttpRequestException("no network")
			: Ok(ThemeFile("ember")));
		var entry = (await gallery.ListAsync()).Single();

		Assert.ThrowsAsync<HttpRequestException>(() => gallery.LoadThemeAsync(entry));
		fail = false;

		Assert.That((await gallery.LoadThemeAsync(entry)).Name, Is.EqualTo("ember"));
	}

	[Test]
	public async Task Reset_ThrowsAwayEverythingFetched()
	{
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();
		await gallery.LoadThemeAsync(entry);

		gallery.Reset();
		await gallery.ListAsync();
		await gallery.LoadThemeAsync(entry);

		Assert.That(handler.Asked.Count(u => u == ThemeGallery.IndexUrl), Is.EqualTo(2));
		Assert.That(handler.Asked.Count(u => u == ThemeGallery.ThemeUrl("ember")), Is.EqualTo(2));
	}

	// ── The size limits ──────────────────────────────────────────────────────

	[Test]
	public void ACatalogueThatSaysItIsTooBig_IsNotFetched()
	{
		var (gallery, _) = Gallery(_ => Ok(Index("ember"), declaredLength: ThemeGallery.MaxIndexBytes + 1));

		// Refused on its own header, before the body is read at all.
		Assert.ThrowsAsync<InvalidDataException>(() => gallery.ListAsync());
	}

	[Test]
	public void ACatalogueThatLiesAboutItsSize_IsStillCutOff()
	{
		// Content-Length is the server's claim. The read is what makes the limit true.
		var huge = $$"""{"version": 2, "themes": [], "padding": "{{new string('a', ThemeGallery.MaxIndexBytes + 100)}}"}""";
		var (gallery, _) = Gallery(_ => Ok(huge, declaredLength: 10));

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.ListAsync());
	}

	[Test]
	public async Task ACatalogueRightUpToTheLimit_IsStillRead()
	{
		// The limit is a bound, not a tripwire the real file is anywhere near: six themes come to
		// under a kilobyte against 512 KB.
		var (gallery, _) = Gallery(_ => Ok(Index("ember")));

		Assert.That((await gallery.ListAsync()).Single().Id, Is.EqualTo("ember"));
	}

	[Test]
	public async Task AThemeThatSaysItIsTooBig_IsNotFetched()
	{
		var (gallery, _) = Gallery(url => IsIndex(url)
			? Ok(Index("ember"))
			: Ok(ThemeFile("ember"), declaredLength: ThemeGallery.MaxThemeBytes + 1));
		var entry = (await gallery.ListAsync()).Single();

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.LoadThemeAsync(entry));
	}

	[Test]
	public async Task AThemeThatLiesAboutItsSize_IsStillCutOff()
	{
		var huge = $$"""{"id": "ember", "theme": {"brightness": 9}, "padding": "{{new string('a', ThemeGallery.MaxThemeBytes + 100)}}"}""";
		var (gallery, _) = Gallery(url => IsIndex(url) ? Ok(Index("ember")) : Ok(huge, declaredLength: 10));
		var entry = (await gallery.ListAsync()).Single();

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.LoadThemeAsync(entry));
	}
}
