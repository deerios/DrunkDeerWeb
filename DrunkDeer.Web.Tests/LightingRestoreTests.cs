using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins what a lighting restore point can and cannot put back — what makes the theme gallery's
/// "preview for five seconds and revert" honest.
/// </summary>
/// <remarks>
/// Same family as <see cref="ProfileCaptureTests"/>, and for the same reason: the A75 cannot report
/// its lighting back, so a session that has not written a colour does not know what the board looks
/// like. A restore point that claimed otherwise would "revert" the board to the SDK's invented seed
/// — black — and a preview would end by switching the user's backlight off.
/// </remarks>
[TestFixture]
public class LightingRestoreTests
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

	private (byte R, byte G, byte B) ColorOf(DDKey key) => _service.Session!.GetKeyColor(key);

	// ── What a fresh session is allowed to claim ─────────────────────────────

	[Test]
	public void FreshSession_HasNothingToRestore()
	{
		var point = _service.CaptureLighting();

		Assert.Multiple(() =>
		{
			Assert.That(point.CanRestore, Is.False, "nothing on this board was set from here");
			Assert.That(point.Theme, Is.Null, "capturing the seed would 'restore' the board to black");
		});
	}

	[Test]
	public async Task RestoringNothing_LeavesTheBoardAsItIs()
	{
		var point = _service.CaptureLighting();
		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Theme = new KeyboardThemeBuilder().Base(10, 20, 30).Build(),
		});

		await _service.RestoreLightingAsync(point);

		// The alternative would be inventing a state to put the board back to, which is worse than
		// leaving the previewed theme on it. The gallery warns the user up front instead.
		Assert.That(ColorOf(DDKey.Q), Is.EqualTo(((byte)10, (byte)20, (byte)30)));
	}

	// ── Once the session knows what it wrote ─────────────────────────────────

	[Test]
	public async Task AfterALightingWrite_TheColorsComeBack()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(200, 100, 0), Wasd, new RgbColor(10, 20, 30),
			backgroundBrightness: 9, brightness: 9);
		var point = _service.CaptureLighting();

		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Theme = new KeyboardThemeBuilder().Base(0, 255, 70).Build(),
		});
		await _service.RestoreLightingAsync(point);

		Assert.Multiple(() =>
		{
			Assert.That(ColorOf(DDKey.W), Is.EqualTo(((byte)200, (byte)100, (byte)0)), "the highlight");
			Assert.That(ColorOf(DDKey.Q), Is.EqualTo(((byte)10, (byte)20, (byte)30)), "the background");
		});
	}

	[Test]
	public async Task AfterALightingWrite_TheRestorePointCanRestore()
	{
		await _service.ApplyLightingAsync(
			new RgbColor(200, 100, 0), Wasd, new RgbColor(10, 20, 30),
			backgroundBrightness: 9, brightness: 9);

		Assert.That(_service.CaptureLighting().CanRestore, Is.True);
	}

	// ── The states that aren't per-key colours ───────────────────────────────

	[Test]
	public async Task ARunningAnimation_IsCapturedInsteadOfTheColorsUnderneathIt()
	{
		// The two are mutually exclusive on the board: an animation replaced the per-key colours,
		// so restoring those colours would leave the animation off.
		await _service.ApplyLightingAsync(
			new RgbColor(200, 100, 0), Wasd, new RgbColor(10, 20, 30),
			backgroundBrightness: 9, brightness: 9);
		await _service.SetLightingModeAsync(LightingMode.Ripple, brightness: 9, speed: 5);

		var point = _service.CaptureLighting();

		Assert.Multiple(() =>
		{
			Assert.That(point.Mode, Is.EqualTo(LightingMode.Ripple));
			Assert.That(point.Theme, Is.Null, "the colours aren't what's on the board");
		});
	}

	[Test]
	public async Task ARunningAnimation_IsRestarted()
	{
		await _service.SetLightingModeAsync(LightingMode.Ripple, brightness: 9, speed: 5);
		var point = _service.CaptureLighting();

		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Theme = new KeyboardThemeBuilder().Base(0, 255, 70).Build(),
		});
		Assume.That(_service.ActiveMode, Is.Null, "per-key colours end an animation");

		await _service.RestoreLightingAsync(point);

		Assert.That(_service.ActiveMode, Is.EqualTo(LightingMode.Ripple));
	}

	[Test]
	public async Task ABacklightThatWasOff_IsTurnedBackOff()
	{
		await _service.TurnLightingOffAsync();
		var point = _service.CaptureLighting();
		Assume.That(point.Off, Is.True);

		await _service.ApplyProfileAsync(new KeyboardProfile
		{
			Theme = new KeyboardThemeBuilder().Base(0, 255, 70).Build(),
		});
		Assume.That(_service.BacklightOff, Is.False);

		await _service.RestoreLightingAsync(point);

		// Previewing a theme on a board whose light the user had switched off has to give them the
		// dark back, not the colours that happened to be underneath it.
		Assert.That(_service.BacklightOff, Is.True);
	}
}
