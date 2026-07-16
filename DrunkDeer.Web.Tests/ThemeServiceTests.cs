using DrunkDeer;
using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using DrunkDeer.Web.Theming;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins the accent the app themes off: that it follows the colours on the board, and that it falls
/// back to the default red whenever the board has nothing trustworthy to say.
/// </summary>
[TestFixture]
public class ThemeServiceTests
{
	private KeyboardStore _store = null!;
	private KeyboardService _keyboard = null!;
	private ThemeService _themes = null!;

	[SetUp]
	public async Task SetUp()
	{
		_store = new KeyboardStore();
		_keyboard = new KeyboardService(_store, new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance);
		_themes = new ThemeService(_keyboard, _store);
		await _keyboard.ConnectDemoAsync();
	}

	[TearDown]
	public async Task TearDown()
	{
		_themes.Dispose();
		await _keyboard.DisposeAsync();
	}

	private static DDKey[] Wasd => [DDKey.W, DDKey.A, DDKey.S, DDKey.D];

	private static readonly RgbColor Red = new(255, 0, 0);
	private static readonly RgbColor Blue = new(0, 0, 255);

	private static string DefaultAccent => Theme.Accent(Theme.DefaultAccentHue);

	/// <summary>The hue the app's primary accent currently carries.</summary>
	private double PrimaryHue()
	{
		var hex = _themes.Current.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
		var c = new MudBlazor.Utilities.MudColor(hex);
		return Oklch2Srgb.FromSrgb(new Srgb(c.R, c.G, c.B)).H;
	}

	/// <param name="tolerance">
	/// Degrees. The default is loose because the stretch bins hues before emitting them, so a
	/// recovered hue can sit a bin's width or two from the original. Tests whose expected hue is
	/// close to the fallback's red must pass something tighter, or they cannot tell a working
	/// accent from a fallback and prove nothing.
	/// </param>
	private static void AssertHue(double actual, double expected, string message, double tolerance = 12)
	{
		double d = Math.Abs(actual - expected) % 360;
		Assert.That(Math.Min(d, 360 - d), Is.LessThan(tolerance),
			$"{message} (hue was {actual:F1}, wanted {expected:F1})");
	}

	/// <summary>Paints the whole board one colour, at a given brightness.</summary>
	private Task PaintBoard(RgbColor colour, byte brightness = 9) =>
		// The first write of a session sends a theme whose base fills every key not listed, so
		// passing the same colour as both base and override lights the whole board with it.
		_keyboard.ApplyLightingAsync(colour, Wasd, colour, brightness, brightness);

	// ── Falling back ─────────────────────────────────────────────────────────

	/// <remarks>
	/// Over-determined on purpose, and worth being plain about: two independent things force the
	/// fallback here. The <c>ColorsAreKnown</c> gate stops the seed being read at all, and the seed
	/// is black anyway, which yields no hue. Removing the gate does not fail this test. It pins the
	/// property a user would notice rather than the mechanism that delivers it — the mechanism is
	/// pinned by <see cref="AFreshSessionsShadow_IsBlack"/>.
	/// </remarks>
	[Test]
	public void FreshSession_UsesTheDefaultRedAccent()
	{
		Assert.Multiple(() =>
		{
			Assert.That(_keyboard.ColorsAreKnown, Is.False, "precondition");
			Assert.That(_themes.Current.PaletteDark.Primary.ToString(
				MudBlazor.Utilities.MudColorOutputFormats.Hex), Is.EqualTo(DefaultAccent));
		});
	}

	[Test]
	public void AFreshSessionsShadow_IsBlack()
	{
		// The reason the ColorsAreKnown gate is currently belt and braces. If the SDK ever seeds the
		// colour shadow the way it seeds actuation with a made-up 2.0 mm, this fails — and the gate
		// stops being redundant on the same day.
		Assert.That(_keyboard.SnapshotColors(), Is.All.Matches<(int Slot, byte R, byte G, byte B)>(
			c => c is { R: 0, G: 0, B: 0 }));
	}

	[Test]
	public async Task Disconnecting_ReturnsToTheDefaultAccent()
	{
		await PaintBoard(Blue);
		Assert.That(PrimaryHue(), Is.Not.EqualTo(Theme.DefaultAccentHue).Within(12), "precondition: the accent moved");

		await _keyboard.DisconnectAsync();

		// The colour shadow dies with the session without raising a lighting change, which is why
		// the service watches the store as well. Without that the accent would keep the hue of a
		// board that is no longer plugged in.
		Assert.That(_themes.Current.PaletteDark.Primary.ToString(
			MudBlazor.Utilities.MudColorOutputFormats.Hex), Is.EqualTo(DefaultAccent));
	}

	// ── Following the board ──────────────────────────────────────────────────

	[Test]
	public async Task ABlueBoard_GivesABlueAccent()
	{
		await PaintBoard(Blue);
		AssertHue(PrimaryHue(), 264, "a blue board should give a blue accent");
	}

	[Test]
	public async Task ARedBoard_GivesTheBoardsRed_NotTheFallbacksRed()
	{
		// Needs a tight tolerance to say anything. The board's red is OKLCH hue 29.2 and the
		// fallback's is 25.3 — under a loose tolerance this passes with the accent broken, because
		// the thing it falls back to is also red.
		await PaintBoard(Red);
		AssertHue(PrimaryHue(), 29.23, "a red board should give the board's own red", tolerance: 2);
	}

	[Test]
	public async Task TheAccentFollowsTheBulkOfTheBoard_NotAHandfulOfKeys()
	{
		// Four keys red against seventy-eight blue. The stretch gives a hue as much of the palette
		// as it has of the board, so the median lands in the blue.
		await _keyboard.ApplyLightingAsync(Red, Wasd, Blue, 9, 9);

		AssertHue(PrimaryHue(), 264, "four red keys outvoted the rest of the board");
	}

	// ── Brightness ───────────────────────────────────────────────────────────

	[Test]
	public async Task ADimBoard_KeepsItsHue()
	{
		await PaintBoard(Blue, brightness: 1);
		AssertHue(PrimaryHue(), 264, "a dim board lost its hue");
	}

	[Test]
	public async Task ADimBackground_StillOutvotesAHandfulOfBrightKeys()
	{
		// The trap, and the state that actually exposes it. The SDK stores a theme's base colour
		// already dimmed but the per-key overrides at full strength, so this board holds 78 keys of
		// (0, 0, 28) and 4 of (255, 0, 0) at once. Read as stored, the value threshold discards the
		// 78 dim blues as though they were unlit and the whole UI turns red on the strength of four
		// keys. Scaling each colour up by its own brightest channel first is what keeps the vote
		// honest.
		await _keyboard.ApplyLightingAsync(Red, Wasd, Blue, 1, 1);

		var shadow = _keyboard.SnapshotColors();
		Assert.Multiple(() =>
		{
			Assert.That(shadow.Count(c => (c.R, c.G, c.B) is (0, 0, 28)), Is.EqualTo(78),
				"precondition: the background really is stored dimmed");
			Assert.That(shadow.Count(c => (c.R, c.G, c.B) is (255, 0, 0)), Is.EqualTo(4),
				"precondition: the overrides really are stored at full strength");
		});

		AssertHue(PrimaryHue(), 264, "the dim background lost its vote to four bright keys");
	}

	[Test]
	public async Task ADeliberatelyDarkColour_IsReadAsItsHue_NotAsUnlit()
	{
		// A board the user set to a dark blue is a blue board. Only the hue is taken, so how far
		// down it is turned makes no difference.
		//
		// Blue rather than a dark red on purpose: a dark red would fall back to the default red
		// when broken and pass regardless, proving nothing.
		await PaintBoard(new RgbColor(0, 0, 32));
		AssertHue(PrimaryHue(), 264, "a dark blue board was not read as blue");
	}

	[Test]
	public async Task Brightness_DoesNotChangeTheAccent()
	{
		// Only the hue is taken, and brightness scales every channel equally, so turning the board
		// down must move nothing. Lightness and chroma are the theme's, fixed.
		await PaintBoard(Blue, brightness: 9);
		var bright = _themes.Current.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);

		await PaintBoard(Blue, brightness: 2);
		var dim = _themes.Current.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);

		Assert.That(dim, Is.EqualTo(bright));
	}

	// ── Publishing ───────────────────────────────────────────────────────────

	[Test]
	public async Task Changed_FiresWhenTheAccentMoves()
	{
		int fired = 0;
		_themes.Changed += () => fired++;

		await PaintBoard(Blue);

		Assert.That(fired, Is.GreaterThan(0));
	}

	[Test]
	public async Task Changed_StaysQuietWhenTheAccentDoesNotMove()
	{
		// MudThemeProvider regenerates its stylesheet whenever the theme object changes, and the
		// events feeding the service fire on plenty of things that leave the colours alone.
		await PaintBoard(Blue);

		int fired = 0;
		_themes.Changed += () => fired++;
		await PaintBoard(Blue);

		Assert.That(fired, Is.Zero, "re-writing the same colour restyled the app");
	}

	[Test]
	public async Task MutedTextStaysGrey_WhateverTheBoardsColourIs()
	{
		// The app uses Color.Secondary for its caption and help text everywhere, so this token is
		// the colour of most of the prose in the UI rather than a brand accent. Letting it follow
		// the keyboard painted every caption red on a red board, which reads as errors throughout.
		// Only the primary accent may move.
		foreach (var colour in new[] { Red, Blue, new RgbColor(0, 255, 0) })
		{
			await _keyboard.DisconnectAsync();
			await _keyboard.ConnectDemoAsync();
			await PaintBoard(colour);

			var hex = _themes.Current.PaletteDark.Secondary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
			var c = new MudBlazor.Utilities.MudColor(hex);

			Assert.That(c.R, Is.EqualTo(c.G).And.EqualTo(c.B),
				$"a #{colour.R:x2}{colour.G:x2}{colour.B:x2} board tinted the muted text colour to {hex}");
		}
	}

	[Test]
	public async Task TheAccentKeepsTheThemesLightness_WhateverTheBoardsColourIs()
	{
		// The board's raw colour is unusable as an accent: at full saturation and value a yellow
		// glares and a blue sinks. Only the hue is taken; L and C are the theme's.
		foreach (var colour in new[] { Red, Blue, new RgbColor(255, 255, 0), new RgbColor(0, 255, 0) })
		{
			await _keyboard.DisconnectAsync();
			await _keyboard.ConnectDemoAsync();
			await PaintBoard(colour);

			var hex = _themes.Current.PaletteDark.Primary.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
			var c = new MudBlazor.Utilities.MudColor(hex);
			var accent = Oklch2Srgb.FromSrgb(new Srgb(c.R, c.G, c.B));

			Assert.That(accent.L, Is.EqualTo(Theme.AccentLightness).Within(0.02),
				$"#{colour.R:x2}{colour.G:x2}{colour.B:x2} produced an accent of the wrong lightness");
		}
	}
}
