using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins the translation from a gallery theme's free-text name to a profile name.
/// </summary>
/// <remarks>
/// The two name spaces are not the same: a theme is called whatever its author called it, while a
/// profile name has to satisfy <see cref="ProfileLibrary.IsValidName"/> because the <c>deerkb</c>
/// CLI turns one into a file path. Every name this produces therefore has to be one the library
/// will actually accept — a copy that throws on save is the failure mode here.
/// </remarks>
[TestFixture]
public class ThemeGalleryTests
{
	// ── Translating the name ─────────────────────────────────────────────────

	[Test]
	public void ANameThatIsAlreadyValid_IsLeftAlone()
	{
		Assert.That(ThemeGallery.ToProfileName("Ember"), Is.EqualTo("Ember"));
	}

	[Test]
	public void Spaces_BecomeUnderscores()
	{
		Assert.That(ThemeGallery.ToProfileName("Ocean Sunrise"), Is.EqualTo("Ocean_Sunrise"));
	}

	[Test]
	public void RunsOfPunctuation_CollapseToOneUnderscore()
	{
		Assert.That(ThemeGallery.ToProfileName("Neon // Nights!!"), Is.EqualTo("Neon_Nights"));
	}

	[Test]
	public void LeadingAndTrailingPunctuation_IsDropped()
	{
		Assert.That(ThemeGallery.ToProfileName("  Ember  "), Is.EqualTo("Ember"));
	}

	[Test]
	public void ANameWithNothingUsableInIt_StillProducesAName()
	{
		// A profile has to be called something, and "" is not a name the library accepts.
		Assert.That(ThemeGallery.ToProfileName("!!!"), Is.EqualTo("Theme"));
	}

	[Test]
	public void AnOverlongName_IsCutToWhatTheLibraryAccepts()
	{
		var name = ThemeGallery.ToProfileName(new string('a', 200));

		Assert.That(name, Has.Length.EqualTo(64));
		Assert.That(ProfileLibrary.IsValidName(name), Is.True);
	}

	[TestCase("Ember")]
	[TestCase("Ocean Sunrise")]
	[TestCase("Neon // Nights!!")]
	[TestCase("!!!")]
	[TestCase("日本語")]
	public void WhateverItIsHandedItProducesAValidProfileName(string themeName)
	{
		Assert.That(ProfileLibrary.IsValidName(ThemeGallery.ToProfileName(themeName)), Is.True);
	}

	// ── Making it unique ─────────────────────────────────────────────────────

	[Test]
	public void AFreeName_IsUsedAsIs()
	{
		Assert.That(ThemeGallery.UniqueName("Ember", ["Nord"]), Is.EqualTo("Ember"));
	}

	[Test]
	public void ATakenName_IsNumbered()
	{
		// A copy is a starting point people edit, so copying twice is normal and must not
		// overwrite the first copy.
		Assert.That(ThemeGallery.UniqueName("Ember", ["Ember"]), Is.EqualTo("Ember-1"));
	}

	[Test]
	public void TheNumberSkipsPastEveryNameAlreadyTaken()
	{
		Assert.That(ThemeGallery.UniqueName("Ember", ["Ember", "Ember-1", "Ember-2"]), Is.EqualTo("Ember-3"));
	}

	[Test]
	public void AGapInTheNumbering_IsFilled()
	{
		Assert.That(ThemeGallery.UniqueName("Ember", ["Ember", "Ember-2"]), Is.EqualTo("Ember-1"));
	}

	[Test]
	public void NamesAreComparedIgnoringCase()
	{
		// These are file names in the CLI, and on Windows "ember" and "Ember" are the same file.
		Assert.That(ThemeGallery.UniqueName("Ember", ["ember"]), Is.EqualTo("Ember-1"));
	}

	[Test]
	public void TheNumberFitsInsideTheLengthLimitRatherThanPushingPastIt()
	{
		var taken = new string('a', 64);

		var name = ThemeGallery.UniqueName(taken, [taken]);

		// Numbering a name that is already at the limit has to shorten it, or the copy would be
		// rejected by the very rule the numbering exists to satisfy.
		Assert.That(name, Has.Length.EqualTo(64));
		Assert.That(name, Does.EndWith("-1"));
		Assert.That(ProfileLibrary.IsValidName(name), Is.True);
	}
}
