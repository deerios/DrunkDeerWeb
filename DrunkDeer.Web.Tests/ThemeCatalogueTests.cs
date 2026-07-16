using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Reading the shared catalogue and the theme files it points at, neither of which this app writes.
/// </summary>
/// <remarks>
/// <para>
/// The rules a submission is held to live in the themes repository, in JavaScript, and are enforced
/// by a workflow with no human in it. This is the app declining to take that on faith. Nothing here
/// is about GitHub being untrustworthy — it is that "the file came from GitHub" is a fact about the
/// transport and says nothing about what is in it, and the checks that fill it are three commits and
/// one language away from here.
/// </para>
/// <para>
/// The bar every test below holds to: a theme that is wrong is dropped, the catalogue around it is
/// not, and nothing that survives can make the app do something it would not otherwise do.
/// </para>
/// <para>
/// The two halves are read separately because they arrive separately. <c>index.json</c> says what
/// the themes are called; a theme's own file says what it looks like, and is fetched only when a
/// card is waiting to draw it.
/// </para>
/// </remarks>
[TestFixture]
public class ThemeCatalogueTests
{
	/// <summary>A catalogue holding exactly the entries given, as JSON.</summary>
	private static string Catalogue(params string[] entries) =>
		$$"""{"version": 2, "themes": [{{string.Join(",", entries)}}]}""";

	/// <summary>One entry with nothing wrong with it, for tests that want to break exactly one thing.</summary>
	private static string Entry(string id = "ember", string name = "Ember", string author = "DrunkDeer",
		string submittedBy = "deerios", int issue = 4) =>
		$$"""{"id": "{{id}}", "name": "{{name}}", "author": "{{author}}", "submittedBy": "{{submittedBy}}", "issue": {{issue}}}""";

	private static IReadOnlyList<GalleryEntry> Read(string json) => ThemeGallery.ReadIndex(json);

	// ── The happy path ───────────────────────────────────────────────────────

	[Test]
	public void AnEntryThatIsFine_IsRead()
	{
		var themes = Read(Catalogue(Entry()));

		Assert.That(themes, Has.Count.EqualTo(1));
		Assert.That(themes[0].Id, Is.EqualTo("ember"));
		Assert.That(themes[0].Name, Is.EqualTo("Ember"));
		Assert.That(themes[0].Author, Is.EqualTo("DrunkDeer"));
		Assert.That(themes[0].SubmittedBy, Is.EqualTo("deerios"));
		Assert.That(themes[0].Issue, Is.EqualTo(4));
	}

	[Test]
	public void AnEmptyCatalogue_IsNotAnError()
	{
		// The gallery decides what to show when there is nothing; this only reports it.
		Assert.That(Read(Catalogue()), Is.Empty);
	}

	// ── The catalogue itself ─────────────────────────────────────────────────

	[Test]
	public void AVersionThisAppDoesNotKnow_IsRefusedOutright()
	{
		// A version bump means the file has been rearranged. Reading it anyway would be guessing.
		Assert.Throws<InvalidDataException>(() => Read("""{"version": 3, "themes": []}"""));
	}

	[Test]
	public void TheOldCatalogueThatCarriedItsThemes_IsRefusedRatherThanHalfRead()
	{
		// Version 1 inlined every theme's lighting. Its entries would read here as themes with no
		// lighting anywhere, and the gallery would fetch six files that do not exist. Refusing on
		// the version is the whole reason there is a version.
		var v1 = """{"version": 1, "themes": [{"id": "ember", "name": "Ember", "author": "x", "theme": {"brightness": 9}}]}""";

		Assert.Throws<InvalidDataException>(() => Read(v1));
	}

	[Test]
	public void SomethingThatIsNotJson_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() => Read("<html>404</html>"));
	}

	[Test]
	public void ACatalogueWithNoThemesList_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() => Read("""{"version": 2}"""));
	}

	// ── One bad entry is one bad entry ───────────────────────────────────────

	[Test]
	public void ABrokenEntry_DoesNotTakeTheGoodOnesWithIt()
	{
		// The point of the whole design: a catalogue is other people's work in one file.
		var themes = Read(Catalogue(
			Entry(id: "ember", name: "Ember"),
			"""{"id": "broken", "name": "Broken", "author": "x", "issue": "not a number"}""",
			Entry(id: "nord", name: "Nord")));

		Assert.That(themes.Select(t => t.Id), Is.EqualTo(new[] { "ember", "nord" }));
	}

	// ── Text that goes on a card ─────────────────────────────────────────────

	[Test]
	public void AnOverlongName_IsNotDrawn()
	{
		Assert.That(Read(Catalogue(Entry(name: new string('a', 41)))), Is.Empty);
	}

	[Test]
	public void AnOverlongCredit_IsNotDrawn()
	{
		Assert.That(Read(Catalogue(Entry(author: new string('a', 41)))), Is.Empty);
	}

	[Test]
	public void ANameCarryingControlCharacters_IsNotDrawn()
	{
		Assert.That(Read(Catalogue(Entry(name: "Em\\u0007ber"))), Is.Empty);
	}

	[Test]
	public void ANameCarryingARightToLeftOverride_IsNotDrawn()
	{
		// U+202E reverses the text after it, which rearranges the sentence the card puts it in.
		Assert.That(Read(Catalogue(Entry(name: "Em\\u202Eber"))), Is.Empty);
	}

	[Test]
	public void ANameOfNothingButPunctuation_IsNotDrawn()
	{
		Assert.That(Read(Catalogue(Entry(name: "!!!"))), Is.Empty);
	}

	[Test]
	public void AnEmptyName_IsNotDrawn()
	{
		Assert.That(Read(Catalogue(Entry(name: ""))), Is.Empty);
	}

	[Test]
	public void ANameInAnotherScript_IsPerfectlyFine()
	{
		// The limits are about drawing a card, not about being English.
		var themes = Read(Catalogue(Entry(name: "日本語")));

		Assert.That(themes, Has.Count.EqualTo(1));
		Assert.That(themes[0].Name, Is.EqualTo("日本語"));
	}

	[Test]
	public void ANameThatLooksLikeMarkup_IsCarriedThroughUntouched()
	{
		// Deliberately not sanitised here: Blazor escapes what it renders, and a gallery that
		// silently rewrote people's theme names would be a worse bug than the one it prevented.
		var themes = Read(Catalogue(Entry(name: "<script>x</script>")));

		Assert.That(themes, Has.Count.EqualTo(1));
		Assert.That(themes[0].Name, Is.EqualTo("<script>x</script>"));
	}

	// ── Ids ──────────────────────────────────────────────────────────────────

	[TestCase("../../elsewhere")]
	[TestCase("Ember")]
	[TestCase("has space")]
	[TestCase("-leading")]
	[TestCase("trailing-")]
	[TestCase("")]
	public void AnIdThatIsNotOneTheRepositoryWouldMake_IsRefused(string id)
	{
		Assert.That(Read(Catalogue(Entry(id: id))), Is.Empty);
	}

	[Test]
	public void AnIdIsWhatAThemesFileIsFetchedFrom_SoItCannotPointElsewhere()
	{
		// The id goes straight into a URL, and this is the check that makes that safe. Every id that
		// survives ReadIndex is lower-case letters, digits and single dashes, so there is nothing in
		// one that could leave the themes folder.
		foreach (var id in Read(Catalogue(Entry(id: "ocean-sunrise"))).Select(t => t.Id))
			Assert.That(ThemeGallery.ThemeUrl(id),
				Is.EqualTo("https://raw.githubusercontent.com/deerios/DrunkDeerThemes/main/themes/ocean-sunrise.json"));
	}

	[Test]
	public void TwoThemesWithOneId_OnlyTheFirstIsShown()
	{
		// The id is what says which card is previewing, and which file to fetch. Two answering to it
		// makes both ambiguous.
		var themes = Read(Catalogue(Entry(id: "ember", name: "Ember"), Entry(id: "ember", name: "Impostor")));

		Assert.That(themes, Has.Count.EqualTo(1));
		Assert.That(themes[0].Name, Is.EqualTo("Ember"));
	}

	// ── Who published it ─────────────────────────────────────────────────────

	[Test]
	public void AnEntryWithNoSubmitter_IsStillShown_ButBelongsToNobody()
	{
		// It decides only whether the theme appears under "My themes". Dropping the entry over it
		// would lose a theme everybody can see to a field only its author uses.
		var themes = Read(Catalogue("""{"id": "ember", "name": "Ember", "author": "x", "issue": 1}"""));

		Assert.That(themes, Has.Count.EqualTo(1));
		Assert.That(themes[0].SubmittedBy, Is.Empty);
	}

	[TestCase("not a login")]
	[TestCase("-leading")]
	[TestCase("trailing-")]
	[TestCase("two--hyphens")]
	public void ASubmitterThatIsNotAGitHubName_IsIgnoredRatherThanMatched(string who)
	{
		// Compared against a name the user typed on the Settings page, so a catalogue that could put
		// anything here could claim a theme for a name nobody could type — or for one they could.
		var themes = Read(Catalogue(Entry(submittedBy: who)));

		Assert.That(themes, Has.Count.EqualTo(1), "the theme itself is fine");
		Assert.That(themes[0].SubmittedBy, Is.Empty, "but nobody published it");
	}

	[Test]
	public void ASubmitterWithAHyphenInTheMiddle_IsARealName()
	{
		Assert.That(Read(Catalogue(Entry(submittedBy: "a-real-name")))[0].SubmittedBy, Is.EqualTo("a-real-name"));
	}

	// ── A theme's own file ───────────────────────────────────────────────────

	private static readonly GalleryEntry Ember = new("ember", "Ember", "DrunkDeer", "deerios", 4);

	/// <summary>One theme file, as the themes repository writes it.</summary>
	private static string File(string theme, string id = "ember") =>
		$$"""{"id": "{{id}}", "name": "Ember", "author": "DrunkDeer", "submittedBy": "deerios", "issue": 4, "theme": {{theme}}}""";

	private static KeyboardTheme ReadFile(string json) => ThemeGallery.ReadThemeFile(json, Ember);

	[Test]
	public void AThemeFile_IsReadForItsLighting()
	{
		var theme = ReadFile(File("""{"brightness": 9, "baseColor": {"r": 120, "g": 20, "b": 0}}"""));

		Assert.That(theme.BaseColor.R, Is.EqualTo(120));
		Assert.That(theme.Brightness, Is.EqualTo(9));
	}

	[Test]
	public void PerKeyColours_SurviveWithTheirKeys()
	{
		var theme = ReadFile(File(
			"""{"brightness": 9, "baseColor": {"r": 0, "g": 0, "b": 0}, "keys": {"W": {"r": 255, "g": 120, "b": 0}}}"""));

		Assert.That(theme.Keys!["W"].R, Is.EqualTo(255));
	}

	[Test]
	public void AFileThatIsNotJson_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() => ReadFile("<html>404</html>"));
	}

	[Test]
	public void AFileWithNoLightingInIt_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() => ReadFile("""{"id": "ember", "name": "Ember"}"""));
	}

	[Test]
	public void AFileThatCallsItselfSomethingElse_IsRefused()
	{
		// Fetched by id, so a file with another id in it is a repository disagreeing with its own
		// catalogue. The alternative is drawing one theme under another's name.
		Assert.Throws<InvalidDataException>(() =>
			ReadFile(File("""{"brightness": 9, "baseColor": {"r": 1, "g": 1, "b": 1}}""", id: "something-else")));
	}

	[Test]
	public void AColourOutOfRange_IsRefused()
	{
		// r/g/b land in bytes, so 300 is not a colour and never becomes a clamped one.
		Assert.Throws<InvalidDataException>(() =>
			ReadFile(File("""{"brightness": 9, "baseColor": {"r": 300, "g": 0, "b": 0}}""")));
	}

	[Test]
	public void ABrightnessPastNine_IsRefused()
	{
		// The SDK throws on this at apply time. Better to say the theme did not load than to hand
		// somebody a Preview button that fails.
		Assert.Throws<InvalidDataException>(() =>
			ReadFile(File("""{"brightness": 10, "baseColor": {"r": 1, "g": 1, "b": 1}}""")));
	}

	[Test]
	public void ABaseBrightnessPastNine_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() =>
			ReadFile(File("""{"brightness": 9, "baseBrightness": 10, "baseColor": {"r": 1, "g": 1, "b": 1}}""")));
	}

	[Test]
	public void AKeyThisSdkDoesNotHave_IsRefused()
	{
		Assert.Throws<InvalidDataException>(() => ReadFile(File(
			"""{"brightness": 9, "baseColor": {"r": 1, "g": 1, "b": 1}, "keys": {"Sausage": {"r": 1, "g": 1, "b": 1}}}""")));
	}

	[Test]
	public void AKeyNamedAsANumber_IsRefused()
	{
		// Enum.TryParse would read "5" as whichever key is fifth, which is nobody's intent. Matched
		// against the names instead, so a number is not a key.
		Assert.Throws<InvalidDataException>(() => ReadFile(File(
			"""{"brightness": 9, "baseColor": {"r": 1, "g": 1, "b": 1}, "keys": {"5": {"r": 1, "g": 1, "b": 1}}}""")));
	}

	[Test]
	public void AKeyNameInTheWrongCase_IsStillThatKey()
	{
		// The SDK reads key names without regard to case, so this gallery has no business being
		// stricter than the thing it is feeding.
		var theme = ReadFile(File(
			"""{"brightness": 9, "baseColor": {"r": 1, "g": 1, "b": 1}, "keys": {"w": {"r": 1, "g": 1, "b": 1}}}"""));

		Assert.That(theme.Keys, Has.Count.EqualTo(1));
	}
}
