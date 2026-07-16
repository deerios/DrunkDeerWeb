using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins the link that publishes a theme.
/// </summary>
/// <remarks>
/// <para>
/// The whole feature is this string, so this is the whole feature under test. What it cannot check
/// is the half of the agreement that lives in another repository: the query parameters below are
/// named after field ids in <c>deerios/DrunkDeerThemes</c>'s issue form, and a wrong name there is
/// ignored in silence — the form simply comes up empty. Nothing here would notice.
/// </para>
/// <para>
/// The length tests are the ones worth keeping. A prefilled form is a URL with an entire keyboard's
/// colours escaped into it, and past GitHub's limit the answer is an error page instead of the form.
/// </para>
/// </remarks>
[TestFixture]
public class ThemePublisherTests
{
	private static KeyboardTheme Theme() => new KeyboardThemeBuilder()
		.Base(0, 40, 120)
		.BaseBrightness(4)
		.Brightness(9)
		.Keys([DDKey.W, DDKey.A, DDKey.S, DDKey.D], 255, 120, 0)
		.Build();

	/// <summary>
	/// A colour that varies from key to key rather than repeating, so a theme built from it deflates
	/// the way a real one would rather than unrealistically well. A theme where every key shares one
	/// colour is not the size this exists to measure.
	/// </summary>
	private static (byte R, byte G, byte B) Vary(int index) => ((byte)(index * 37 % 256), (byte)(index * 61 % 256), (byte)(index * 89 % 256));

	/// <summary>A theme giving every key on the A75 a colour of its own: the realistic biggest.</summary>
	/// <remarks>
	/// Off the real layout rather than every <see cref="DDKey"/> there is, because that is what the
	/// app can actually capture from the one keyboard this SDK is proven against. A board with a
	/// numpad would make a bigger one — see the test below.
	/// </remarks>
	private static KeyboardTheme WholeA75()
	{
		KeyGeometry.TryGetKeys("a75", "ansi", out var layout);
		var builder = new KeyboardThemeBuilder().Base(10, 20, 30).Brightness(9);
		var i = 0;
		foreach (var key in layout!)
		{
			var (r, g, b) = Vary(i++);
			builder.Key(key.Key, r, g, b);
		}
		return builder.Build();
	}

	/// <summary>A theme that sets every key the SDK knows of, on any model.</summary>
	private static KeyboardTheme EveryKey()
	{
		var builder = new KeyboardThemeBuilder().Base(10, 20, 30).Brightness(9);
		var i = 0;
		foreach (var key in Enum.GetValues<DDKey>())
		{
			var (r, g, b) = Vary(i++);
			builder.Key(key, r, g, b);
		}
		return builder.Build();
	}

	/// <summary>The length of the URL a signed-out click is actually 302'd to — see <see cref="ThemePublisher.Fits"/>.</summary>
	private static int SignedOutLength(string url) => "https://github.com/login?return_to=".Length + Uri.EscapeDataString(url).Length;

	/// <summary>
	/// The other side of <see cref="ThemePublisher.ToPacked"/>: what the themes repository's Node
	/// does to a <c>z1.</c> payload, done here in C# so the round trip can be pinned without a second
	/// repository to hand.
	/// </summary>
	private static string Unpack(string packed)
	{
		Assert.That(packed, Does.StartWith("z1."));
		var base64 = packed["z1.".Length..].Replace('-', '+').Replace('_', '/');
		base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
		var compressed = Convert.FromBase64String(base64);

		using var input = new MemoryStream(compressed);
		using var deflate = new DeflateStream(input, CompressionMode.Decompress);
		using var output = new MemoryStream();
		deflate.CopyTo(output);
		return Encoding.UTF8.GetString(output.ToArray());
	}

	// ── The theme JSON ───────────────────────────────────────────────────────

	[Test]
	public void TheJson_IsTheThemeTheSdkWouldReadBack()
	{
		var theme = JsonSerializer.Deserialize<KeyboardTheme>(
			ThemePublisher.ToJson(Theme()),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

		Assert.Multiple(() =>
		{
			Assert.That(theme.BaseColor.G, Is.EqualTo(40));
			Assert.That(theme.BaseBrightness, Is.EqualTo(4));
			Assert.That(theme.Brightness, Is.EqualTo(9));
			Assert.That(theme.Keys!["W"].R, Is.EqualTo(255));
		});
	}

	[Test]
	public void TheJson_IsCompact()
	{
		// Not a style preference: this goes in a URL, where an indent is three characters per space
		// once escaped, and the size of it is what decides whether the link works at all.
		Assert.That(ThemePublisher.ToJson(Theme()), Does.Not.Contain("\n"));
	}

	[Test]
	public void TheJson_LeavesOutWhatTheThemeDoesNotSay()
	{
		var plain = new KeyboardThemeBuilder().Base(255, 244, 214).Brightness(9).Build();
		// baseBrightness is nullable and null means "don't dim the base". Writing it as null would
		// be a third state to the reader on the other end, which has only two.
		Assert.That(ThemePublisher.ToJson(plain), Does.Not.Contain("baseBrightness"));
	}

	// ── The link ─────────────────────────────────────────────────────────────

	[Test]
	public void TheUrl_OpensTheThemeFormOnTheThemesRepository()
	{
		var url = ThemePublisher.BuildIssueUrl("Ocean Sunrise", "addi", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.StartWith($"https://github.com/{ThemePublisher.Repository}/issues/new?"));
			Assert.That(url, Does.Contain("template=new-theme.yml"));
		});
	}

	[Test]
	public void TheUrl_CarriesTheNameTheCreditAndTheTheme()
	{
		var url = ThemePublisher.BuildIssueUrl("Ocean Sunrise", "addi", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Contain("theme-name=Ocean%20Sunrise"));
			Assert.That(url, Does.Contain("credit=addi"));
			// z1. and its base64url payload are unreserved, so EscapeDataString leaves them alone —
			// this is the packed form, not the escaped JSON the URL used to carry.
			Assert.That(url, Does.Contain("theme-json=z1."));
			Assert.That(url, Does.Contain("title=Theme%3A%20Ocean%20Sunrise"));
		});
	}

	[Test]
	public void TheUrl_EscapesANameThatWouldOtherwiseBeReadAsMoreParameters()
	{
		// A theme name is free text and these are the characters that would end the parameter and
		// start another one — the difference between a name and a way to set fields it shouldn't.
		var url = ThemePublisher.BuildIssueUrl("Rock & Roll #1", "", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Contain("theme-name=Rock%20%26%20Roll%20%231"));
			Assert.That(url.Split("theme-name=")[1].Split('&')[0], Is.EqualTo("Rock%20%26%20Roll%20%231"));
		});
	}

	[Test]
	public void TheUrl_LeavesTheCreditOutWhenThereIsNone()
	{
		// The form reads an empty credit as "use my GitHub username", which it can only do if the
		// parameter is absent rather than present and blank.
		Assert.That(ThemePublisher.BuildIssueUrl("Ember", "   ", Theme()), Does.Not.Contain("credit="));
	}

	[Test]
	public void TheUrl_TrimsWhatWasTyped()
	{
		Assert.That(ThemePublisher.BuildIssueUrl("  Ember  ", "  addi  ", Theme()), Does.Contain("theme-name=Ember&"));
	}

	[Test]
	public void TheUrl_DoesNotTickTheConfirmation()
	{
		// It cannot — GitHub does not prefill checkboxes — and it should not: agreeing to publish is
		// the submitter's answer to give. Pinned so that a later attempt to be helpful gets caught.
		Assert.That(ThemePublisher.BuildIssueUrl("Ember", "addi", Theme()), Does.Not.Contain("confirm"));
	}

	// ── Length ───────────────────────────────────────────────────────────────

	[Test]
	public void AThemeColouringTheWholeA75_FitsInTheUrlEvenSignedOut()
	{
		// The regression that matters: a signed-out click never requests this URL, it is 302'd to
		// /login?return_to=<this URL, escaped a second time>, and that is the one that has to be
		// under GitHub's limit. Against the old escaped-JSON encoding this is an ~8500-character
		// signed-out URL — a 502 for anyone not already signed in, while Fits said it was fine.
		var url = ThemePublisher.BuildIssueUrl("Whole Board", "addi", WholeA75());

		Assert.That(ThemePublisher.Fits(url), Is.True,
			$"A theme colouring every A75 key makes a {SignedOutLength(url)}-character signed-out " +
			$"URL, over the {ThemePublisher.MaxUrlLength} limit.");
	}

	[Test]
	public void AThemeColouringEveryKeyOnAnyModel_AlsoFitsNow()
	{
		// The premise of the old "too big to carry" test is gone: packed, even the biggest theme the
		// SDK can describe — every key on every model — fits signed out, with room to spare. The
		// clipboard fallback is not dead code even so; see AThemeTooBigToCarry_IsReportedRatherThanSent.
		var url = ThemePublisher.BuildIssueUrl("Every Key", "addi", EveryKey());

		Assert.That(ThemePublisher.Fits(url), Is.True,
			$"Every key on every model makes a {SignedOutLength(url)}-character signed-out URL, " +
			$"over the {ThemePublisher.MaxUrlLength} limit.");
	}

	[Test]
	public void Fits_ChecksTheEscapedLengthNotTheRawOne()
	{
		// The doubling only bites on characters that need escaping, and a packed theme's own payload
		// is all unreserved base64url — so a theme this size no longer proves the signed-out check by
		// itself (see the two tests above). A name is still free text, and enough wide characters in
		// it re-create the gap on their own: 220 of them make a ~4200-character URL — nowhere near
		// MaxUrlLength on its own — that escapes to a ~6900-character signed-out one, over it. A
		// `Fits` that measured url.Length would wrongly call this one fine.
		var url = ThemePublisher.BuildIssueUrl(new string('日', 220), "addi", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url.Length, Is.LessThan(ThemePublisher.MaxUrlLength));
			Assert.That(SignedOutLength(url), Is.GreaterThan(ThemePublisher.MaxUrlLength));
			Assert.That(ThemePublisher.Fits(url), Is.False);
		});
	}

	[Test]
	public void AThemeTooBigToCarry_IsReportedRatherThanSent()
	{
		// With themes packing this small, a pathological name is the only thing left that can make
		// Fits fail — this test is now the sole reason the clipboard fallback is reachable, and the
		// only thing keeping it honest rather than dead code nobody would notice going stale.
		var url = ThemePublisher.BuildIssueUrl(new string('x', ThemePublisher.MaxUrlLength), "addi", Theme());
		Assert.That(ThemePublisher.Fits(url), Is.False);
	}

	[Test]
	public void ThePackedPayload_SurvivesEscapingUnchanged()
	{
		// The entire point of base64url over base64: every character of its alphabet, plus the `.`
		// separating the z1 prefix, is unreserved in RFC 3986, so this string is what goes in the URL
		// whether it is escaped once (a signed-in submitter) or twice (the login round trip).
		var packed = ThemePublisher.ToPacked(Theme());
		Assert.That(Uri.EscapeDataString(packed), Is.EqualTo(packed));
	}

	[Test]
	public void ThePackedPayload_UnpacksBackToTheSameTheme()
	{
		var theme = WholeA75();
		var unpacked = Unpack(ThemePublisher.ToPacked(theme));

		Assert.That(unpacked, Is.EqualTo(ThemePublisher.ToJson(theme)));

		var roundTripped = JsonSerializer.Deserialize<KeyboardTheme>(
			unpacked, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
		Assert.That(roundTripped.Keys!["W"].R, Is.EqualTo(theme.Keys!["W"].R));
	}

	[Test]
	public void TheEmptyForm_IsTheSameFormWithNothingInIt()
	{
		var url = ThemePublisher.BuildEmptyIssueUrl();

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Contain("template=new-theme.yml"));
			Assert.That(url, Does.Not.Contain("theme-json"));
			Assert.That(ThemePublisher.Fits(url), Is.True);
		});
	}

	// ── Updating a theme you published ───────────────────────────────────────

	[Test]
	public void TheUpdateUrl_OpensTheUpdateFormWithTheIdAndTheTheme()
	{
		var url = ThemePublisher.BuildUpdateIssueUrl("ocean-sunrise", "Ocean Sunrise", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.StartWith("https://github.com/deerios/DrunkDeerThemes/issues/new?"));
			Assert.That(url, Does.Contain("template=update-theme.yml"));
			Assert.That(url, Does.Contain("theme-id=ocean-sunrise"));
			Assert.That(url, Does.Contain("theme-json=z1."));
			Assert.That(url, Does.Contain("title=Update%20theme%3A%20Ocean%20Sunrise"));
		});
	}

	[Test]
	public void TheUpdateUrl_CarriesNoNameAndNoCredit()
	{
		// An update replaces the lighting and nothing else — the two things somebody might argue
		// about are the ones it cannot touch. The form has no field for either, so sending one would
		// be a parameter GitHub silently ignores and a promise this app cannot keep.
		var url = ThemePublisher.BuildUpdateIssueUrl("ocean-sunrise", "Ocean Sunrise", Theme());

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Not.Contain("theme-name="));
			Assert.That(url, Does.Not.Contain("credit="));
		});
	}

	[Test]
	public void TheUpdateUrl_SaysNothingAboutWhoIsAsking()
	{
		// It could not, and must not look as though it did: whether an update is allowed rests on the
		// GitHub account that submits the issue, checked at the other end against the account that
		// published the theme. Nothing the app puts in a URL grants it anything.
		var url = ThemePublisher.BuildUpdateIssueUrl("ocean-sunrise", "Ocean Sunrise", Theme());

		Assert.That(url.ToLowerInvariant(), Does.Not.Contain("submitted"));
		Assert.That(url.ToLowerInvariant(), Does.Not.Contain("user"));
	}

	[Test]
	public void AWholeA75Update_FitsInTheUrlEvenSignedOut()
	{
		// Same rule the publish link is held to, and the reason the packed form exists at all: a
		// signed-out click is 302'd to /login with the whole URL escaped a second time.
		var url = ThemePublisher.BuildUpdateIssueUrl("ocean-sunrise", "Ocean Sunrise", WholeA75());

		Assert.That(ThemePublisher.Fits(url), Is.True,
			$"signed out this is {SignedOutLength(url)} characters, against a ceiling of {ThemePublisher.MaxUrlLength}");
	}

	[Test]
	public void TheEmptyUpdateForm_StillCarriesTheId()
	{
		// The clipboard fallback: the theme is too big to travel, but the id is a few characters and
		// asking somebody to paste their theme is bad enough without also asking them to know it.
		var url = ThemePublisher.BuildEmptyUpdateIssueUrl("ocean-sunrise", "Ocean Sunrise");

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Contain("template=update-theme.yml"));
			Assert.That(url, Does.Contain("theme-id=ocean-sunrise"));
			Assert.That(url, Does.Not.Contain("theme-json"));
			Assert.That(ThemePublisher.Fits(url), Is.True);
		});
	}

	// ── Unpublishing a theme you published ───────────────────────────────────

	[Test]
	public void TheRemoveUrl_OpensTheRemoveFormWithTheId()
	{
		var url = ThemePublisher.BuildRemoveIssueUrl("ocean-sunrise", "Ocean Sunrise");

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Contain("template=remove-theme.yml"));
			Assert.That(url, Does.Contain("theme-id=ocean-sunrise"));
			Assert.That(url, Does.Contain("title=Remove%20theme%3A%20Ocean%20Sunrise"));
		});
	}

	[Test]
	public void TheRemoveUrl_CarriesNoTheme_SoItAlwaysFits()
	{
		// Nothing about a theme's size can make its removal link too long to click, which would be a
		// poor way to be stuck with a theme.
		var url = ThemePublisher.BuildRemoveIssueUrl(new string('a', 48), new string('n', 40));

		Assert.Multiple(() =>
		{
			Assert.That(url, Does.Not.Contain("theme-json"));
			Assert.That(ThemePublisher.Fits(url), Is.True);
		});
	}

	[Test]
	public void AnIdIsEscapedIntoTheUrl_LikeEverythingElse()
	{
		// Ids out of the catalogue cannot hold anything that needs it — ThemeGallery.ReadIndex sees
		// to that — but this method does not know where its id came from, and being right only
		// because of a check somewhere else is how that check gets deleted.
		var url = ThemePublisher.BuildRemoveIssueUrl("a b&c", "X");

		Assert.That(url, Does.Contain("theme-id=a%20b%26c"));
	}
}
