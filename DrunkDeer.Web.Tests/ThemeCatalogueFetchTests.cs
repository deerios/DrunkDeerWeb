using System.Net;
using System.Text;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
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

	/// <summary>A gallery with a store of its own that starts empty, and the network behind it.</summary>
	/// <remarks>
	/// <paramref name="storage"/> is for the tests that care what is in the store — either to seed it,
	/// to read what was written, or to keep one gallery's store when a second is built over it, which
	/// is how a second page load is staged.
	/// </remarks>
	private static (ThemeGallery Gallery, Stub Handler) Gallery(
		Func<string, HttpResponseMessage> reply, FakeBrowserStorage? storage = null)
	{
		var handler = new Stub(reply);
		var cache = new ThemeCache(new BrowserStorage(storage ?? new FakeBrowserStorage()));
		return (new ThemeGallery(null!, new HttpClient(handler), cache, NullLogger<ThemeGallery>.Instance), handler);
	}

	private static string Index(params string[] ids) => IndexOf(ids.Select(id => (id, (int?)null)).ToArray());

	/// <summary>A catalogue that says which version each theme is. A null version says nothing at all.</summary>
	/// <remarks>
	/// Concatenated rather than interpolated, for the reason given on <see cref="ThemeFile"/>: the
	/// entry ends in a run of braces and a raw interpolated literal turns that into a puzzle.
	/// </remarks>
	private static string IndexOf(params (string Id, int? Version)[] themes)
	{
		var entries = themes.Select(t =>
			"{\"id\": \"" + t.Id + "\", \"name\": \"" + t.Id + "\", \"author\": \"DrunkDeer\", " +
			"\"submittedBy\": \"deerios\", \"issue\": 1" +
			(t.Version is { } v ? ", \"version\": " + v : "") + "}");

		return "{\"version\": 2, \"themes\": [" + string.Join(",", entries) + "]}";
	}

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
	public async Task AFirstVersionOfATheme_IsFetchedPlain()
	{
		// A theme nobody has updated is at the address it has always been at, so every cache between
		// here and GitHub goes on answering for it. Busting these unconditionally would cost the
		// sharing that makes paging cheap and buy nothing.
		var (gallery, handler) = Gallery(Repository("ember"));
		var entry = (await gallery.ListAsync()).Single();

		await gallery.LoadThemeAsync(entry);

		Assert.That(handler.Requested, Does.Contain(ThemeGallery.ThemeUrl("ember")));
	}

	[Test]
	public async Task AnUpdatedTheme_IsFetchedFromAnAddressTheCachesHaveNotSeen()
	{
		// The trap the version has to spring. An update rewrites themes/<id>.json in place, and both
		// GitHub's CDN and the browser hold that address for far longer than an update takes — so a
		// refetch that asked the old question would be answered with the picture from before it, and
		// the version would have bought nothing.
		var (gallery, handler) = Gallery(url =>
			IsIndex(url) ? Ok(IndexOf(("ember", 4))) : Ok(ThemeFile("ember")));
		var entry = (await gallery.ListAsync()).Single();

		await gallery.LoadThemeAsync(entry);

		var asked = handler.Requested.Single(u => !IsIndex(u));
		Assert.That(asked, Is.Not.EqualTo(ThemeGallery.ThemeUrl("ember")), "the address must differ from the old version's");
		Assert.That(asked, Is.EqualTo(ThemeGallery.ThemeUrl("ember", 4)));
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

	// ── Kept between page loads ──────────────────────────────────────────────

	/// <summary>Where <see cref="ThemeCache"/> puts a theme. Spelled out rather than asked for.</summary>
	/// <remarks>
	/// A test that built the key the same way the code does would pass whatever the code did with it,
	/// including using a different key on every load. This is the key, written down.
	/// </remarks>
	private static string StoredKey(string id) => "drunkdeer.theme." + id;

	/// <summary>A repository that stops answering for themes once the first load is done.</summary>
	/// <remarks>
	/// How "it did not fetch" is asserted without trusting a call count: if the second load reaches
	/// the network at all, it fails outright rather than quietly passing.
	/// </remarks>
	private static Func<string, HttpResponseMessage> Offline(params string[] ids) => url =>
		IsIndex(url) ? Ok(Index(ids)) : throw new HttpRequestException("no network");

	[Test]
	public async Task AThemeAlreadyLookedAt_IsNotFetchedOnTheNextPageLoad()
	{
		// The point of the whole thing: a page turn fetches twelve theme files, and browsing the
		// gallery, closing the tab and coming back should not cost them all again.
		var storage = new FakeBrowserStorage();
		var (first, _) = Gallery(Repository("ember"), storage);
		await first.LoadThemeAsync((await first.ListAsync()).Single());

		// A second gallery over the same storage: a new page load, in every way that matters here.
		var (second, _) = Gallery(Offline("ember"), storage);
		var theme = await second.LoadThemeAsync((await second.ListAsync()).Single());

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
	}

	[Test]
	public async Task AnUpdatedTheme_IsNotAnsweredWithTheStoredCopy()
	{
		// The bug the version exists to stop, and the reason an id alone is not a key: the file at
		// themes/<id>.json is rewritten by an update, so a store keyed by id would go on showing the
		// old picture on every load from here on, and no amount of refreshing would shift it.
		var storage = new FakeBrowserStorage();
		var (first, _) = Gallery(Repository("ember"), storage);
		await first.LoadThemeAsync((await first.ListAsync()).Single());

		// The same theme, now version 2, with different lighting behind it.
		var updated = ThemeFile("ember").Replace("\"r\": 120", "\"r\": 7");
		var (second, _) = Gallery(url => IsIndex(url) ? Ok(IndexOf(("ember", 2))) : Ok(updated), storage);
		var theme = await second.LoadThemeAsync((await second.ListAsync()).Single());

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(7), "the updated picture, not the stored one");
	}

	[Test]
	public async Task AnUpdatedTheme_ReplacesTheCopyItSupersedes()
	{
		// One key per theme, not one per version. Nothing here can enumerate localStorage to go and
		// collect the old ones, so an update that left its predecessor behind would fill the origin up
		// with revisions nothing will ever read again.
		var storage = new FakeBrowserStorage();
		var (first, _) = Gallery(Repository("ember"), storage);
		await first.LoadThemeAsync((await first.ListAsync()).Single());

		var (second, _) = Gallery(url => IsIndex(url) ? Ok(IndexOf(("ember", 2))) : Ok(ThemeFile("ember")), storage);
		await second.LoadThemeAsync((await second.ListAsync()).Single());

		Assert.That(storage.Stored.Keys, Is.EqualTo(new[] { StoredKey("ember") }));
	}

	[Test]
	public async Task OnlyTheThemesLookedAt_AreStored()
	{
		var storage = new FakeBrowserStorage();
		var (gallery, _) = Gallery(Repository("ember", "nord", "matrix"), storage);
		var entries = await gallery.ListAsync();

		await gallery.LoadThemeAsync(entries.Single(e => e.Id == "nord"));

		Assert.That(storage.Stored.Keys, Is.EqualTo(new[] { StoredKey("nord") }));
	}

	[Test]
	public async Task TheCatalogue_IsNeverStored()
	{
		// It is the thing that says which stored themes are still true, so it has to come from the
		// repository every time. Storing it would also be the stale gallery ThemeGallery refuses to
		// have — see the note there about there being no offline fallback.
		var storage = new FakeBrowserStorage();
		var (gallery, _) = Gallery(Repository("ember"), storage);

		await gallery.ListAsync();

		Assert.That(storage.Stored, Is.Empty);
	}

	[Test]
	public async Task AThemeThatWouldNotDraw_IsNotStored()
	{
		// Storing it would mean failing the same check on every load for ever, over a file the
		// repository is going to serve again anyway.
		var storage = new FakeBrowserStorage();
		var (gallery, _) = Gallery(url => IsIndex(url) ? Ok(Index("ember")) : Ok("<html>nope</html>"), storage);
		var entry = (await gallery.ListAsync()).Single();

		Assert.ThrowsAsync<InvalidDataException>(() => gallery.LoadThemeAsync(entry));
		Assert.That(storage.Stored, Is.Empty);
	}

	[Test]
	public async Task AStoredThemeIsReadTheWayAFetchedOneIs()
	{
		// This is a string out of the user's own browser: anybody with the devtools open can write it,
		// and it must not be a way to get a theme onto the page that a fetch would have refused.
		var storage = new FakeBrowserStorage();
		storage.Stored[StoredKey("ember")] =
			"""{"version": 1, "file": "{\"id\": \"ember\", \"theme\": {\"brightness\": 99}}"}""";
		var (gallery, handler) = Gallery(Repository("ember"), storage);

		var theme = await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		// Brightness 99 is off the scale the keyboard has, so the stored copy is refused — and the
		// repository's answer, which is fine, is what gets drawn.
		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
		Assert.That(handler.Requested, Does.Contain(ThemeGallery.ThemeUrl("ember")));
	}

	[Test]
	public async Task AStoredThemeThatIsRefused_IsClearedOutRatherThanLeftToFailForEver()
	{
		var storage = new FakeBrowserStorage();
		storage.Stored[StoredKey("ember")] = """{"version": 1, "file": "<html>nope</html>"}""";
		var (gallery, _) = Gallery(Repository("ember"), storage);

		await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		Assert.That(storage.Stored[StoredKey("ember")], Does.Not.Contain("nope"), "the bad copy should be gone");
		Assert.That(storage.Stored[StoredKey("ember")], Does.Contain("baseColor"), "and the fetched one kept in its place");
	}

	[Test]
	public async Task ARubbishValueUnderTheKey_IsJustAMiss()
	{
		var storage = new FakeBrowserStorage();
		storage.Stored[StoredKey("ember")] = "not json at all";
		var (gallery, _) = Gallery(Repository("ember"), storage);

		var theme = await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
	}

	[Test]
	public async Task AStoredThemeOverTheSizeLimit_IsRefused()
	{
		// A theme too big to have been fetched must not become drawable by being put in the store.
		var storage = new FakeBrowserStorage();
		var padding = new string('a', ThemeGallery.MaxThemeBytes + 1);
		storage.Stored[StoredKey("ember")] = $$"""{"version": 1, "file": "{{padding}}"}""";
		var (gallery, handler) = Gallery(Repository("ember"), storage);

		await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		Assert.That(handler.Requested, Does.Contain(ThemeGallery.ThemeUrl("ember")));
	}

	[Test]
	public async Task ABrowserThatRefusesSiteData_IsJustAGalleryThatFetches()
	{
		// Storage can be blocked outright, or the origin can be full — 500 themes will not all fit.
		// Neither is worth a word to the user: it is the gallery it was before any of this existed.
		var storage = new FakeBrowserStorage { Fault = new JSException("storage is blocked") };
		var (gallery, _) = Gallery(Repository("ember"), storage);

		var theme = await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
	}

	[Test]
	public async Task Refreshing_GoesPastTheStoredCopyToo()
	{
		// A Refresh that fetched the catalogue and then answered every card out of localStorage would
		// be a Refresh that refreshes nothing the user can see.
		var storage = new FakeBrowserStorage();
		var (gallery, handler) = Gallery(Repository("ember"), storage);
		var entry = (await gallery.ListAsync()).Single();
		await gallery.LoadThemeAsync(entry);

		gallery.Reset();
		await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		Assert.That(handler.Asked.Count(u => u == ThemeGallery.ThemeUrl("ember")), Is.EqualTo(2));
	}

	[Test]
	public async Task AfterARefresh_TheStoreIsTrustedAgain()
	{
		// The distrust is one fetch long, not for the rest of the page: once the theme has been
		// fetched again, the stored copy is the network's answer and there is nothing to distrust.
		var storage = new FakeBrowserStorage();
		var (gallery, handler) = Gallery(Repository("ember"), storage);
		var entry = (await gallery.ListAsync()).Single();
		await gallery.LoadThemeAsync(entry);

		gallery.Reset();
		await gallery.LoadThemeAsync((await gallery.ListAsync()).Single());

		// A third page load, with nothing to fetch from: the refetched copy must have been kept.
		var (later, _) = Gallery(Offline("ember"), storage);
		var theme = await later.LoadThemeAsync((await later.ListAsync()).Single());

		Assert.That(theme.Theme.BaseColor.R, Is.EqualTo(120));
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
