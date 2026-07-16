using DrunkDeer.Web.Theming;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins the behaviour of the hue_stretch port that the profile-driven accent rests on.
/// </summary>
/// <remarks>
/// The property that matters is proportionality: a hue should occupy as much of the palette as it
/// occupies of the board. Everything the theme does — reading the median for the primary accent,
/// collapsing to one colour on a single-hue board — follows from that, so it is asserted directly
/// rather than by pinning whatever bytes the code happens to emit today.
/// </remarks>
[TestFixture]
public class HueStretchTests
{
	/// <summary>Blur off, so a boundary between two hues stays where the maths put it.</summary>
	private static readonly HueStretchOptions Sharp = new() { Blur = false };

	private static Srgb Hue(float degrees) => HueStretch.FromHsv(degrees, 1f, 1f);

	private static IReadOnlyList<Srgb> Many(Srgb colour, int count) =>
		[.. Enumerable.Repeat(colour, count)];

	/// <summary>The share of palette entries whose hue is within a bin's width of <paramref name="degrees"/>.</summary>
	private static double ShareOf(Srgb[] palette, float degrees, float tolerance = 6f)
	{
		int hits = palette.Count(c =>
		{
			var (h, s, _) = HueStretch.ToHsv(c);
			if (s < 0.1f) return false;
			double d = Math.Abs(h - degrees) % 360;
			return Math.Min(d, 360 - d) <= tolerance;
		});
		return (double)hits / palette.Length;
	}

	// ── The default profile ──────────────────────────────────────────────────

	[Test]
	public void SingleHue_FillsTheWholePalette()
	{
		// The red-and-black default in miniature. Black is dropped before binning, so red is the
		// only hue left, and one hue with a 100% share is given 100% of the palette. This is why
		// the default theme reads as a single red rather than a spectrum — the stretch has nothing
		// to spread until a profile carries more than one colour.
		var palette = HueStretch.Stretch([.. Many(Hue(0), 20), .. Many(new Srgb(0, 0, 0), 62)]);

		Assert.That(ShareOf(palette, 0), Is.EqualTo(1.0).Within(0.01));
	}

	[Test]
	public void Black_ContributesNoHue()
	{
		// An all-black board is the reference implementation's "image with no colour in it": there
		// is no hue to stretch, and it returns white rather than inventing one. ThemeService reads
		// that as "fall back to the default accent".
		var palette = HueStretch.Stretch(Many(new Srgb(0, 0, 0), 82));

		Assert.That(palette, Is.All.EqualTo(new Srgb(255, 255, 255)));
	}

	[Test]
	public void NoSource_IsTreatedTheSameAsNoColour()
	{
		Assert.That(HueStretch.Stretch([]), Is.All.EqualTo(new Srgb(255, 255, 255)));
	}

	[Test]
	public void DarkKeys_AreDroppedBeforeBinning()
	{
		// A key lit far below the value threshold carries a hue in principle, but not one worth
		// theming off. It must not outvote a key that is actually lit.
		var palette = HueStretch.Stretch(
			[.. Many(Hue(120), 4), .. Many(HueStretch.FromHsv(0, 1f, 0.05f), 78)], Sharp);

		Assert.That(ShareOf(palette, 120), Is.EqualTo(1.0).Within(0.01), "the dim red was counted");
	}

	// ── Proportionality ──────────────────────────────────────────────────────

	[Test]
	public void TwoHues_SplitThePaletteByHowManyKeysWearThem()
	{
		var palette = HueStretch.Stretch([.. Many(Hue(0), 41), .. Many(Hue(240), 41)], Sharp);

		Assert.Multiple(() =>
		{
			Assert.That(ShareOf(palette, 0), Is.EqualTo(0.5).Within(0.02));
			Assert.That(ShareOf(palette, 240), Is.EqualTo(0.5).Within(0.02));
		});
	}

	[Test]
	public void AHuesShare_TracksItsKeyCount()
	{
		// The core claim. Three quarters of the keys red means three quarters of the palette red,
		// which is what makes the median a hue weighted by key count.
		var palette = HueStretch.Stretch([.. Many(Hue(0), 60), .. Many(Hue(240), 20)], Sharp);

		Assert.Multiple(() =>
		{
			Assert.That(ShareOf(palette, 0), Is.EqualTo(0.75).Within(0.03));
			Assert.That(ShareOf(palette, 240), Is.EqualTo(0.25).Within(0.03));
		});
	}

	[Test]
	public void AMinorityHue_DoesNotWinTheMedian()
	{
		// One stray green key must not drag the whole UI green.
		var palette = HueStretch.Stretch([.. Many(Hue(0), 81), .. Many(Hue(120), 1)], Sharp);
		var (h, _, _) = HueStretch.ToHsv(palette[palette.Length / 2]);

		Assert.That(h, Is.EqualTo(0).Within(6));
	}

	[Test]
	public void HuesKeepTheirOrder_AroundTheCircle()
	{
		// The stretch walks hues in ascending order, so the lower hue occupies the earlier entries.
		var palette = HueStretch.Stretch([.. Many(Hue(30), 41), .. Many(Hue(300), 41)], Sharp);

		var (first, _, _) = HueStretch.ToHsv(palette[0]);
		var (last, _, _) = HueStretch.ToHsv(palette[^1]);

		Assert.Multiple(() =>
		{
			Assert.That(first, Is.EqualTo(30).Within(6));
			Assert.That(last, Is.EqualTo(300).Within(6));
		});
	}

	// ── Blur ─────────────────────────────────────────────────────────────────

	[Test]
	public void Blur_LeavesASingleHuePaletteAlone()
	{
		// The kernel is normalised, so smoothing a constant palette must return the same constant.
		// If this drifts, the default red theme drifts with it.
		var sharp = HueStretch.Stretch(Many(Hue(0), 82), Sharp);
		var blurred = HueStretch.Stretch(Many(Hue(0), 82), new HueStretchOptions { Blur = true });

		Assert.That(blurred[blurred.Length / 2], Is.EqualTo(sharp[sharp.Length / 2]));
	}

	[Test]
	public void Blur_SoftensTheBoundaryBetweenTwoHues()
	{
		var source = new List<Srgb>([.. Many(Hue(0), 41), .. Many(Hue(240), 41)]);

		var sharp = HueStretch.Stretch(source, Sharp);
		var blurred = HueStretch.Stretch(source, new HueStretchOptions { Blur = true });

		// Either side of the midpoint the sharp palette jumps between two saturated colours; the
		// blurred one mixes them, and mixing opposing hues drains saturation.
		float sharpSat = HueStretch.ToHsv(sharp[sharp.Length / 2]).S;
		float blurredSat = HueStretch.ToHsv(blurred[blurred.Length / 2]).S;

		Assert.That(blurredSat, Is.LessThan(sharpSat), "the boundary was not softened");
	}

	// ── Options ──────────────────────────────────────────────────────────────

	[Test]
	public void DefaultOptions_AreTheRecordsDefaults_NotAZeroFilledStruct()
	{
		// default(HueStretchOptions) is N=0, which would ask for a palette of nothing. Stretch
		// takes a nullable so its no-argument form is the reference implementation's settings.
		Assert.That(HueStretch.Stretch(Many(Hue(0), 8)), Has.Length.EqualTo(new HueStretchOptions().N));
	}

	[Test]
	public void PaletteSize_IsHonoured()
	{
		Assert.That(HueStretch.Stretch(Many(Hue(0), 8), new HueStretchOptions { N = 16 }), Has.Length.EqualTo(16));
	}

	[Test]
	public void PaletteSize_MustBePositive()
	{
		Assert.Throws<ArgumentOutOfRangeException>(
			() => HueStretch.Stretch(Many(Hue(0), 8), new HueStretchOptions { N = 0 }));
	}

	// ── HSV ──────────────────────────────────────────────────────────────────

	[Test]
	public void Hsv_RoundTrips()
	{
		for (float hue = 0; hue < 360; hue += 15)
		{
			var (h, s, v) = HueStretch.ToHsv(HueStretch.FromHsv(hue, 1f, 1f));
			Assert.Multiple(() =>
			{
				Assert.That(h, Is.EqualTo(hue).Within(1), $"hue {hue}");
				Assert.That(s, Is.EqualTo(1f).Within(0.01));
				Assert.That(v, Is.EqualTo(1f).Within(0.01));
			});
		}
	}

	[Test]
	public void Hsv_ReportsGreyAsHueless()
	{
		var (_, s, v) = HueStretch.ToHsv(new Srgb(128, 128, 128));
		Assert.Multiple(() =>
		{
			Assert.That(s, Is.EqualTo(0f).Within(0.01));
			Assert.That(v, Is.EqualTo(0.502f).Within(0.01));
		});
	}
}
