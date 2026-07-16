using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins what <see cref="ActiveProfile"/> claims the board is wearing, and when it says there is
/// something to save back — the two answers the Profiles panel's "Update" button is built on.
/// </summary>
/// <remarks>
/// Driven through a real <see cref="KeyboardService"/> on the simulator rather than a fake, because
/// what is actually being tested is the comparison against a captured board: a stub would only prove
/// that two strings this test wrote are different from each other.
/// </remarks>
[TestFixture]
public class ActiveProfileTests
{
	private KeyboardStore _store = null!;
	private KeyboardService _keyboard = null!;
	private ActiveProfile _active = null!;

	[SetUp]
	public async Task SetUp()
	{
		_store = new KeyboardStore();
		_keyboard = new KeyboardService(_store, new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance);
		await _keyboard.ConnectDemoAsync();
		_active = new ActiveProfile(_keyboard, _store);
	}

	[TearDown]
	public async Task TearDown()
	{
		_active.Dispose();
		await _keyboard.DisposeAsync();
	}

	private static DDKey[] Wasd => [DDKey.W, DDKey.A, DDKey.S, DDKey.D];

	/// <summary>Colours the whole board, which is what makes the session's picture of it real.</summary>
	private Task LightAsync(byte r, byte g, byte b) =>
		_keyboard.ApplyLightingAsync(new RgbColor(r, g, b), Wasd, new RgbColor(0, 0, 40), 4, 9);

	// ── Nothing applied ──────────────────────────────────────────────────────

	[Test]
	public void FreshSession_IsWearingNothing()
	{
		Assert.Multiple(() =>
		{
			Assert.That(_active.Name, Is.Null);
			Assert.That(_active.HasUnsavedChanges, Is.False, "there is no profile for the board to differ from");
		});
	}

	// ── Applying, then editing ───────────────────────────────────────────────

	[Test]
	public void Set_RecordsTheProfileWithNothingToSave()
	{
		_active.Set("Ember");

		Assert.Multiple(() =>
		{
			Assert.That(_active.Name, Is.EqualTo("Ember"));
			Assert.That(_active.HasUnsavedChanges, Is.False, "the board was just captured as this profile");
		});
	}

	[Test]
	public async Task ChangingTheBoard_IsSomethingToSave()
	{
		await LightAsync(255, 0, 0);
		_active.Set("Ember");

		await LightAsync(0, 255, 0);

		Assert.That(_active.HasUnsavedChanges, Is.True);
	}

	[Test]
	public async Task SavingBack_ClearsTheChange()
	{
		await LightAsync(255, 0, 0);
		_active.Set("Ember");
		await LightAsync(0, 255, 0);

		// What the panel does after writing the board back over the profile.
		_active.Set("Ember");

		Assert.That(_active.HasUnsavedChanges, Is.False);
	}

	// The point of comparing snapshots rather than counting edits: a change that has been undone is
	// not a change, however many writes it took to get back.
	[Test]
	public async Task UndoingAChangeByHand_IsNotSomethingToSave()
	{
		await LightAsync(255, 0, 0);
		_active.Set("Ember");

		await LightAsync(0, 255, 0);
		await LightAsync(255, 0, 0);

		Assert.That(_active.HasUnsavedChanges, Is.False, "the board is back to what the profile holds");
	}

	// ── Following the library ────────────────────────────────────────────────

	[Test]
	public void Renamed_FollowsTheProfileInUse()
	{
		_active.Set("Ember");

		_active.Renamed("Ember", "Cinder");

		Assert.That(_active.Name, Is.EqualTo("Cinder"));
	}

	[Test]
	public void Renamed_IgnoresOtherProfiles()
	{
		_active.Set("Ember");

		_active.Renamed("Nord", "Frost");

		Assert.That(_active.Name, Is.EqualTo("Ember"));
	}

	[Test]
	public void Forget_DropsTheProfileInUse()
	{
		_active.Set("Ember");

		_active.Forget("Ember");

		Assert.That(_active.Name, Is.Null, "there is nowhere to save back to any more");
	}

	[Test]
	public void Forget_IgnoresOtherProfiles()
	{
		_active.Set("Ember");

		_active.Forget("Nord");

		Assert.That(_active.Name, Is.EqualTo("Ember"));
	}

	// ── The keyboard going away ──────────────────────────────────────────────

	[Test]
	public async Task Disconnecting_DropsTheProfileInUse()
	{
		_active.Set("Ember");

		await _keyboard.DisconnectAsync();

		Assert.That(_active.Name, Is.Null,
			"the session's picture of the board dies with it, so there is nothing left to compare");
	}

	[Test]
	public async Task Disconnecting_RaisesChanged()
	{
		_active.Set("Ember");
		int changes = 0;
		_active.Changed += () => changes++;

		await _keyboard.DisconnectAsync();

		Assert.That(changes, Is.EqualTo(1), "the panel has to stop offering to update a profile it can't");
	}
}
