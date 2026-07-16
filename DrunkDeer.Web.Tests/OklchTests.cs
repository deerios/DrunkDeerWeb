using DrunkDeer.Web.Theming;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins the OKLCH conversions the theme is built on.
/// </summary>
/// <remarks>
/// Two jobs rest on this maths. The Zits palette is authored in OKLCH and MudBlazor only parses
/// hex, so every surface token passes through it; and the accent hue taken off the keyboard is
/// rebuilt at a fixed lightness and chroma, which is only worth doing if the hue survives the trip.
/// </remarks>
[TestFixture]
public class OklchTests
{
	// ── The anchors ──────────────────────────────────────────────────────────

	[Test]
	public void Achromatic_ExtremesAreBlackAndWhite()
	{
		Assert.Multiple(() =>
		{
			Assert.That(Oklch2Srgb.ToSrgb(new Oklch(1, 0, 0)).ToHex(), Is.EqualTo("#ffffff"));
			Assert.That(Oklch2Srgb.ToSrgb(new Oklch(0, 0, 0)).ToHex(), Is.EqualTo("#000000"));
		});
	}

	[Test]
	public void Achromatic_IsGrey_WhateverTheHueAngleClaims()
	{
		// Chroma 0 means there is no hue to honour, so the angle must not leak into the channels.
		foreach (double hue in new double[] { 0, 90, 180, 270, 359 })
		{
			var c = Oklch2Srgb.ToSrgb(new Oklch(0.5, 0, hue));
			Assert.That(c.R, Is.EqualTo(c.G).And.EqualTo(c.B), $"hue {hue} tinted a grey");
		}
	}

	/// <summary>
	/// The OKLCH lightnesses of Zits' neutral ramp, against the hex Tailwind publishes for the same
	/// steps.
	/// </summary>
	/// <remarks>
	/// Zits' ramp is Tailwind v4's <c>neutral</c> scale, which Tailwind documents in both OKLCH and
	/// hex. That makes this a check against a source outside this repo rather than against whatever
	/// the converter happens to emit: if the maths is wrong, these will not line up by luck.
	/// </remarks>
	[TestCase(0.985, "#fafafa")] // neutral-50
	[TestCase(0.708, "#a1a1a1")] // neutral-400
	[TestCase(0.269, "#262626")] // neutral-800
	[TestCase(0.205, "#171717")] // neutral-900
	[TestCase(0.145, "#0a0a0a")] // neutral-950
	public void ZitsNeutralRamp_MatchesTheHexTailwindPublishes(double lightness, string expected)
	{
		Assert.That(Oklch2Srgb.ToSrgb(new Oklch(lightness, 0, 0)).ToHex(), Is.EqualTo(expected));
	}

	// ── Round trips ──────────────────────────────────────────────────────────

	[Test]
	public void RoundTrip_PreservesAnInGamutColour()
	{
		foreach (var original in new[]
		{
			new Srgb(255, 0, 0), new Srgb(0, 255, 0), new Srgb(0, 0, 255),
			new Srgb(48, 96, 192), new Srgb(200, 180, 40), new Srgb(17, 17, 17),
		})
		{
			var back = Oklch2Srgb.ToSrgb(Oklch2Srgb.FromSrgb(original));

			// A byte of slack each way: the trip is through cube roots and a gamma curve.
			Assert.Multiple(() =>
			{
				Assert.That(back.R, Is.EqualTo(original.R).Within(1), $"R of {original.ToHex()}");
				Assert.That(back.G, Is.EqualTo(original.G).Within(1), $"G of {original.ToHex()}");
				Assert.That(back.B, Is.EqualTo(original.B).Within(1), $"B of {original.ToHex()}");
			});
		}
	}

	[Test]
	public void FromSrgb_ReportsTheHueOfAKnownColour()
	{
		// Zits' red primary is oklch(0.637 0.237 25.331); red should land near that angle.
		var red = Oklch2Srgb.FromSrgb(new Srgb(255, 0, 0));
		Assert.That(red.H, Is.EqualTo(29.23).Within(0.5));
	}

	// ── Gamut mapping ────────────────────────────────────────────────────────

	[Test]
	public void GamutMapping_KeepsTheHueOfAColourTooVividForSrgb()
	{
		// The theme asks for chroma 0.237 at every hue, and sRGB cannot deliver that everywhere —
		// blue especially. The point of reducing chroma rather than clipping channels is that the
		// hue, the one thing taken from the keyboard, comes through unharmed.
		foreach (double hue in new double[] { 0, 60, 120, 180, 240, 300 })
		{
			var asked = new Oklch(Theme.AccentLightness, Theme.AccentChroma, hue);
			var got = Oklch2Srgb.FromSrgb(Oklch2Srgb.ToSrgb(asked));

			Assert.That(Delta(got.H, hue), Is.LessThan(2.0), $"hue {hue} drifted to {got.H:F1}");
		}

		static double Delta(double a, double b)
		{
			double d = Math.Abs(a - b) % 360;
			return Math.Min(d, 360 - d);
		}
	}

	[Test]
	public void GamutMapping_LandsInGamut_AtEveryHueTheThemeCanAskFor()
	{
		for (double hue = 0; hue < 360; hue += 5)
		{
			var mapped = Oklch2Srgb.FromSrgb(
				Oklch2Srgb.ToSrgb(new Oklch(Theme.AccentLightness, Theme.AccentChroma, hue)));

			Assert.That(Oklch2Srgb.InGamut(mapped), Is.True, $"hue {hue} came back out of gamut");
		}
	}

	[Test]
	public void GamutMapping_OnlyEverReducesChroma()
	{
		// A colour that already fits must come back untouched, not "corrected".
		var modest = new Oklch(0.637, 0.02, 200);
		Assert.That(Oklch2Srgb.InGamut(modest), Is.True, "precondition: the test colour fits");

		var back = Oklch2Srgb.FromSrgb(Oklch2Srgb.ToSrgb(modest));
		Assert.That(back.C, Is.EqualTo(modest.C).Within(0.005));
	}

	[Test]
	public void AccentsAreDistinct_AcrossTheHueCircle()
	{
		// If gamut mapping crushed chroma to nothing the accents would all collapse to the same
		// grey, and the whole profile-driven theme would be invisible while still "working".
		var accents = Enumerable.Range(0, 12).Select(i => Theme.Accent(i * 30)).ToList();

		Assert.That(accents.Distinct().Count(), Is.EqualTo(accents.Count),
			"different hues produced the same accent");
	}
}
