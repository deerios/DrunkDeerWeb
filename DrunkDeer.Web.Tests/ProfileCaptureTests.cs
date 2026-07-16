using DrunkDeer;
using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins what <see cref="KeyboardService.CaptureProfile"/> is allowed to claim it knows, and that
/// what it captures survives a round trip back onto the board.
/// </summary>
/// <remarks>
/// The A75 cannot report its settings back, so the session starts with the SDK's invented seed:
/// 2.0 mm on every key, and black. Capturing that as though the user had chosen it is the bug this
/// fixture exists to prevent — the same family as the baselines pinned by ActuationBaselineTests
/// and LightingBaselineTests in the SDK's own suite.
/// </remarks>
[TestFixture]
public class ProfileCaptureTests
{
	private KeyboardService _service = null!;

	[SetUp]
	public async Task SetUp()
	{
		_service = new KeyboardService(
			new KeyboardStore(), new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance);
		await _service.ConnectDemoAsync();
	}

	[TearDown]
	public async Task TearDown() => await _service.DisposeAsync();

	private static DDKey[] Wasd => [DDKey.W, DDKey.A, DDKey.S, DDKey.D];

	// ── What a fresh session is allowed to claim ─────────────────────────────

	[Test]
	public void FreshSession_CapturesNeitherDepthsNorColors()
	{
		var profile = _service.CaptureProfile();

		Assert.Multiple(() =>
		{
			// Both would otherwise serialise the SDK's seed as fact.
			Assert.That(profile.Actuation, Is.Null, "a fresh session has never written a depth");
			Assert.That(profile.Theme, Is.Null, "a fresh session has never written a colour");
		});
	}

	[Test]
	public void FreshSession_StillCapturesRapidTrigger()
	{
		// Unlike depths and colours, this one the board actually reports, in the identity handshake.
		Assert.That(_service.CaptureProfile().RapidTrigger, Is.Not.Null);
	}

	[Test]
	public async Task AfterAnActuationWrite_CapturesDepthsButStillNotColors()
	{
		await _service.ApplyActuationAsync(1.2f, Wasd, baselineMm: 2.0f);

		var profile = _service.CaptureProfile();
		Assert.Multiple(() =>
		{
			Assert.That(profile.Actuation, Is.Not.Null, "the session has now written every depth");
			Assert.That(profile.Theme, Is.Null, "but it still has never written a colour");
		});
	}

	[Test]
	public async Task AfterALightingWrite_CapturesColors()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(0, 0, 40),
			backgroundBrightness: 9, brightness: 7);

		Assert.That(_service.CaptureProfile().Theme, Is.Not.Null);
	}

	// ── The captured shape ───────────────────────────────────────────────────

	[Test]
	public async Task CapturedDepths_UseARealDefault_NotZero()
	{
		await _service.ApplyActuationAsync(1.2f, Wasd, baselineMm: 2.0f);

		var actuation = _service.CaptureProfile().Actuation!;
		Assert.Multiple(() =>
		{
			// A zero default means "leave the keys I didn't list alone", which would make this
			// profile land differently depending on what the session it's applied to had written.
			Assert.That(actuation.Default, Is.Not.Zero);
			Assert.That(actuation.Default, Is.EqualTo(2.0f).Within(0.001f), "the value most keys share");
			// Only the exceptions are listed, not all 82 keys.
			Assert.That(actuation.Keys, Has.Count.EqualTo(Wasd.Length));
		});
	}

	[Test]
	public async Task CapturedDepths_ListTheKeysThatDiffer()
	{
		await _service.ApplyActuationAsync(1.2f, Wasd, baselineMm: 2.0f);

		var keys = _service.CaptureProfile().Actuation!.Keys!;
		foreach (var key in Wasd)
			Assert.That(keys[key.ToString()], Is.EqualTo(1.2f).Within(0.001f), $"{key} was set individually");
	}

	[Test]
	public async Task CapturedDepths_UniformBoard_ListsNoExceptions()
	{
		var all = _service.Session!.Layout.Select(k => k.Key).ToArray();
		await _service.ApplyActuationAsync(1.5f, all, baselineMm: 1.5f);

		var actuation = _service.CaptureProfile().Actuation!;
		Assert.Multiple(() =>
		{
			Assert.That(actuation.Default, Is.EqualTo(1.5f).Within(0.001f));
			Assert.That(actuation.Keys, Is.Null.Or.Empty, "nothing differs from the default");
		});
	}

	[Test]
	public async Task CapturedTheme_DoesNotReapplyBackgroundBrightness()
	{
		// The SDK scales BaseBrightness into the colours it stores, so a capture that set it again
		// would dim the background a second time on every save/apply round trip.
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(90, 180, 270 % 256),
			backgroundBrightness: 3, brightness: 7);

		var theme = _service.CaptureProfile().Theme!;
		Assert.That(theme.BaseBrightness, Is.Null);
	}

	[Test]
	public async Task CapturedTheme_KeepsTheBrightnessThatWasSent()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(0, 0, 40),
			backgroundBrightness: 9, brightness: 7);

		Assert.That(_service.CaptureProfile().Theme!.Brightness, Is.EqualTo(7));
	}

	[Test]
	public async Task CapturedTheme_SeparatesBackgroundFromTheKeysThatDiffer()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(0, 0, 40),
			backgroundBrightness: 9, brightness: 7);

		var theme = _service.CaptureProfile().Theme!;
		Assert.Multiple(() =>
		{
			Assert.That((theme.BaseColor.R, theme.BaseColor.G, theme.BaseColor.B), Is.EqualTo(((byte)0, (byte)0, (byte)40)));
			Assert.That(theme.Keys, Has.Count.EqualTo(Wasd.Length));
			foreach (var key in Wasd)
			{
				var c = theme.Keys![key.ToString()];
				Assert.That((c.R, c.G, c.B), Is.EqualTo(((byte)255, (byte)0, (byte)0)));
			}
		});
	}

	// ── Round trip ───────────────────────────────────────────────────────────

	[Test]
	public async Task Capture_Serialise_Apply_RestoresTheSameDepths()
	{
		await _service.ApplyActuationAsync(1.2f, Wasd, baselineMm: 2.0f);
		var expected = _service.GetActuationProfile().ToDictionary(kv => kv.Key, kv => kv.Value);

		// Through JSON, because that is how a saved profile actually comes back.
		var json = _service.CaptureProfile().ToJson();
		await _service.DisconnectAsync();
		await _service.ConnectDemoAsync();

		await _service.ApplyProfileAsync(KeyboardProfile.FromJson(json));

		var restored = _service.GetActuationProfile();
		foreach (var (key, depthMm) in expected)
			Assert.That(restored[key], Is.EqualTo(depthMm).Within(0.001f), $"{key} came back at a different depth");
	}

	[Test]
	public async Task Capture_Serialise_Apply_RestoresTheSameColors()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(0, 0, 40),
			backgroundBrightness: 9, brightness: 7);
		var expected = _service.SnapshotColors().ToDictionary(c => c.Slot, c => (c.R, c.G, c.B));

		var json = _service.CaptureProfile().ToJson();
		await _service.DisconnectAsync();
		await _service.ConnectDemoAsync();

		await _service.ApplyProfileAsync(KeyboardProfile.FromJson(json));

		foreach (var (slot, r, g, b) in _service.SnapshotColors())
			Assert.That((r, g, b), Is.EqualTo(expected[slot]), $"slot {slot} came back a different colour");
	}

	[Test]
	public async Task ApplyingACapturedProfile_MakesTheSessionsPictureTrue()
	{
		await _service.ApplyActuationAsync(1.2f, Wasd, baselineMm: 2.0f);
		await _service.ApplyLightingAsync(
			new RgbColor(255, 0, 0), Wasd, new RgbColor(0, 0, 40),
			backgroundBrightness: 9, brightness: 7);
		var json = _service.CaptureProfile().ToJson();

		await _service.DisconnectAsync();
		await _service.ConnectDemoAsync();
		Assert.Multiple(() =>
		{
			Assert.That(_service.DepthsAreKnown, Is.False, "a new session knows nothing");
			Assert.That(_service.ColorsAreKnown, Is.False);
		});

		await _service.ApplyProfileAsync(KeyboardProfile.FromJson(json));

		Assert.Multiple(() =>
		{
			// A captured profile writes every key, which is exactly what earns these flags: the
			// session's picture is no longer inherited from the SDK's seed.
			Assert.That(_service.DepthsAreKnown, Is.True);
			Assert.That(_service.ColorsAreKnown, Is.True);
		});
	}

	[Test]
	public async Task ApplyingADepthProfileThatPreserves_DoesNotClaimTheDepthsAreKnown()
	{
		// The trap: a zero default writes only the listed keys and leaves the rest at whatever the
		// session held — which on a fresh session is the invented seed, not the board. Nothing was
		// learned, so nothing may be claimed.
		var preserving = new KeyDepthProfileBuilder().Default(0f).Keys(Wasd, 1.2f).Build();

		await _service.ApplyProfileAsync(new KeyboardProfile { Actuation = preserving });

		Assert.That(_service.DepthsAreKnown, Is.False);
	}

	// ── Rapid trigger sensitivity ────────────────────────────────────────────

	[Test]
	public void FreshSession_CapturesNoSensitivity()
	{
		var profile = _service.CaptureProfile();

		Assert.Multiple(() =>
		{
			// Same reason as the depths: the session seeds both points at an invented 0.25 mm and
			// the board has never been asked, so there is nothing here anyone chose.
			Assert.That(_service.SensitivityIsKnown, Is.False);
			Assert.That(profile.Downstroke, Is.Null);
			Assert.That(profile.Upstroke, Is.Null);
		});
	}

	[Test]
	public async Task AfterASensitivityWrite_CapturesBothPoints()
	{
		await _service.ApplySensitivityAsync(
			downstrokeMm: 0.4f, upstrokeMm: 0.6f, Wasd,
			baselineDownstrokeMm: 0.25f, baselineUpstrokeMm: 0.25f);

		var profile = _service.CaptureProfile();
		Assert.Multiple(() =>
		{
			Assert.That(_service.SensitivityIsKnown, Is.True);
			Assert.That(profile.Downstroke, Is.Not.Null);
			Assert.That(profile.Upstroke, Is.Not.Null);
			// Writing sensitivity says nothing about the actuation points.
			Assert.That(profile.Actuation, Is.Null, "a sensitivity write is not a depth write");
		});
	}

	[Test]
	public async Task CapturedSensitivity_ListsTheKeysThatDiffer()
	{
		await _service.ApplySensitivityAsync(0.4f, 0.6f, Wasd, 0.25f, 0.25f);

		var down = _service.CaptureProfile().Downstroke!;
		var up = _service.CaptureProfile().Upstroke!;
		Assert.Multiple(() =>
		{
			// A real default, never zero: zero means "leave the unlisted keys alone", which would
			// land differently depending on what the target session had already written.
			Assert.That(down.Default, Is.EqualTo(0.25f).Within(0.001f));
			Assert.That(up.Default, Is.EqualTo(0.25f).Within(0.001f));
			Assert.That(down.Keys!.Keys, Is.EquivalentTo(Wasd.Select(k => k.ToString())));
			Assert.That(up.Keys!.Keys, Is.EquivalentTo(Wasd.Select(k => k.ToString())));
		});
	}

	[Test]
	public async Task ApplyingASensitivityProfile_MakesTheSessionsPictureTrue()
	{
		await _service.ApplySensitivityAsync(0.4f, 0.6f, Wasd, 0.25f, 0.25f);
		var saved = _service.CaptureProfile();

		// A second session has learned nothing, so it may claim nothing…
		await _service.DisposeAsync();
		_service = new KeyboardService(
			new KeyboardStore(), new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance);
		await _service.ConnectDemoAsync();
		Assert.That(_service.SensitivityIsKnown, Is.False, "a reconnect starts back at the seed");

		// …until a profile carrying real defaults writes every key.
		await _service.ApplyProfileAsync(saved);

		Assert.Multiple(() =>
		{
			Assert.That(_service.SensitivityIsKnown, Is.True);
			Assert.That(_service.GetDownstrokeProfile()[DDKey.W], Is.EqualTo(0.4f).Within(0.001f));
			Assert.That(_service.GetUpstrokeProfile()[DDKey.W], Is.EqualTo(0.6f).Within(0.001f));
		});
	}

	[Test]
	public async Task HalfASensitivityProfile_DoesNotCountAsKnowing()
	{
		// Either point alone leaves the other at the seed, so the pair still isn't known. A
		// hand-written profile is free to carry only one.
		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Downstroke = new KeyDepthProfileBuilder().Default(0.3f).Build(),
		});

		Assert.That(_service.SensitivityIsKnown, Is.False);
	}

	[Test]
	public async Task PreservingSensitivityProfile_DoesNotCountAsKnowing()
	{
		// The zero-default trap, for sensitivity: writes only the listed keys and leaves the rest
		// at the session's seed, so nothing was learned.
		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Downstroke = new KeyDepthProfileBuilder().Default(0f).Keys(Wasd, 0.4f).Build(),
			Upstroke = new KeyDepthProfileBuilder().Default(0f).Keys(Wasd, 0.6f).Build(),
		});

		Assert.That(_service.SensitivityIsKnown, Is.False);
	}
}
