using DrunkDeer.Protocol;
using DrunkDeer.Web.Theming;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins <see cref="ThemePalette"/> against the SDK's own theme-apply path, which it exists to
/// mirror for the case where there is no session to ask.
/// </summary>
/// <remarks>
/// The gallery draws a theme through this and then writes the same theme to the board through
/// <c>KeyboardSession.ApplyThemeAsync</c>. If the two disagree the thumbnail is a lie about what
/// the hardware will do, which is exactly the bug a preview button is supposed to make unnecessary.
/// </remarks>
[TestFixture]
public class ThemePaletteTests
{
	[Test]
	public void UnlistedKeys_WearTheBaseColor()
	{
		var palette = new ThemePalette(new KeyboardThemeBuilder().Base(10, 20, 30).Build());

		Assert.That(palette[DDKey.Q], Is.EqualTo(((byte)10, (byte)20, (byte)30)));
	}

	[Test]
	public void ListedKeys_WearTheirOverride()
	{
		var palette = new ThemePalette(new KeyboardThemeBuilder()
			.Base(10, 20, 30)
			.Keys([DDKey.W, DDKey.A], 200, 100, 0)
			.Build());

		Assert.Multiple(() =>
		{
			Assert.That(palette[DDKey.W], Is.EqualTo(((byte)200, (byte)100, (byte)0)));
			Assert.That(palette[DDKey.A], Is.EqualTo(((byte)200, (byte)100, (byte)0)));
			Assert.That(palette[DDKey.S], Is.EqualTo(((byte)10, (byte)20, (byte)30)), "not listed");
		});
	}

	// The two brightnesses are the thing most easily got backwards, and getting them backwards is
	// invisible except as "the picture doesn't look like the keyboard".
	[Test]
	public void BaseBrightness_DimsTheBaseColor()
	{
		var palette = new ThemePalette(new KeyboardThemeBuilder()
			.Base(90, 180, 255)
			.BaseBrightness(3)
			.Build());

		// The same scaling RgbColor.Scale does: level/9 per channel.
		Assert.That(palette[DDKey.Q], Is.EqualTo(((byte)30, (byte)60, (byte)85)));
	}

	[Test]
	public void BaseBrightness_LeavesPerKeyOverridesAlone()
	{
		var palette = new ThemePalette(new KeyboardThemeBuilder()
			.Base(90, 90, 90)
			.BaseBrightness(3)
			.Keys([DDKey.W], 240, 240, 240)
			.Build());

		// The point of BaseBrightness: a dim background under highlights that are not dimmed,
		// which the firmware's single brightness byte cannot express on its own.
		Assert.Multiple(() =>
		{
			Assert.That(palette[DDKey.W], Is.EqualTo(((byte)240, (byte)240, (byte)240)));
			Assert.That(palette[DDKey.Q], Is.EqualTo(((byte)30, (byte)30, (byte)30)));
		});
	}

	// Brightness is one firmware byte for the whole frame, not part of any key's colour — the
	// session doesn't fold it into the colours it reports, so neither does this.
	[Test]
	public void Brightness_DoesNotChangeAnyKeysColor()
	{
		var dim = new ThemePalette(new KeyboardThemeBuilder().Base(90, 90, 90).Brightness(1).Build());
		var bright = new ThemePalette(new KeyboardThemeBuilder().Base(90, 90, 90).Brightness(9).Build());

		Assert.That(dim[DDKey.Q], Is.EqualTo(bright[DDKey.Q]));
	}

	[Test]
	public void AKeyNameItDoesNotRecognise_IsSkippedRatherThanThrowing()
	{
		// The SDK logs and skips these on apply; a shared catalogue will carry themes written for
		// boards with keys this one hasn't got.
		var theme = new KeyboardThemeBuilder().Base(10, 20, 30).Build();
		theme.Keys = new Dictionary<string, KeyColor> { ["NotAKeyOnAnyBoard"] = new() { R = 1, G = 2, B = 3 } };

		var palette = new ThemePalette(theme);

		Assert.That(palette[DDKey.Q], Is.EqualTo(((byte)10, (byte)20, (byte)30)));
	}

	[Test]
	public void KeyNames_AreMatchedIgnoringCase()
	{
		// Enum.TryParse ignoreCase in the SDK's apply path, so a hand-written theme saying "w"
		// lands on W there — and must here too.
		var theme = new KeyboardThemeBuilder().Base(10, 20, 30).Build();
		theme.Keys = new Dictionary<string, KeyColor> { ["w"] = new() { R = 200, G = 100, B = 0 } };

		Assert.That(new ThemePalette(theme)[DDKey.W], Is.EqualTo(((byte)200, (byte)100, (byte)0)));
	}
}
