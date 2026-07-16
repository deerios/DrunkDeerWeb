using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins what <see cref="SessionRestore"/> puts on a keyboard when it connects, and what it keeps so
/// that there is anything to put back.
/// </summary>
/// <remarks>
/// The startup action writes a whole board's worth of settings to real hardware without being asked
/// twice, so the cases that matter most here are the ones where it should do nothing at all.
/// </remarks>
[TestFixture]
public class SessionRestoreTests
{
	private const string LastChangesKey = "drunkdeer.lastchanges";

	private KeyboardStore _store = null!;
	private KeyboardService _keyboard = null!;
	private FakeProfileStorage _profileStorage = null!;
	private ProfileLibrary _profiles = null!;
	private ActiveProfile _active = null!;
	private FakeBrowserStorage _js = null!;
	private SettingsService _settings = null!;
	private SessionRestore _restore = null!;

	[SetUp]
	public async Task SetUp()
	{
		_store = new KeyboardStore();
		_keyboard = new KeyboardService(_store, new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance);
		await _keyboard.ConnectDemoAsync();

		_profileStorage = new FakeProfileStorage();
		_profiles = new ProfileLibrary(_profileStorage);
		_active = new ActiveProfile(_keyboard, _store);

		_js = new FakeBrowserStorage();
		_settings = new SettingsService(new BrowserStorage(_js));

		_restore = new SessionRestore(
			_keyboard, _store, _profiles, _active, _settings, new BrowserStorage(_js),
			NullLogger<SessionRestore>.Instance);
	}

	[TearDown]
	public async Task TearDown()
	{
		_restore.Dispose();
		_active.Dispose();
		await _keyboard.DisposeAsync();
	}

	/// <summary>A profile that colours the whole board, so applying it visibly lands.</summary>
	private static KeyboardProfile Green() => new()
	{
		Theme = new KeyboardThemeBuilder().Base(0, 255, 0).Brightness(9).Build(),
	};

	private Task UseAsync(StartupAction action, string? profile = null) =>
		_settings.SaveAsync(new AppSettings { Startup = action, StartupProfile = profile });

	private (byte R, byte G, byte B) ColorOf(DDKey key) => _keyboard.Session!.GetKeyColor(key);

	// ── Doing nothing ────────────────────────────────────────────────────────

	[Test]
	public async Task Nothing_LeavesTheBoardAlone()
	{
		await UseAsync(StartupAction.Nothing);

		Assert.That(await _restore.ApplyStartupAsync(), Is.Null, "there is nothing to report");
		Assert.That(_keyboard.ColorsAreKnown, Is.False, "nothing was written");
	}

	[Test]
	public async Task Disconnected_DoesNothing()
	{
		await _profiles.SaveAsync("Ember", Green());
		await UseAsync(StartupAction.Profile, "Ember");
		await _keyboard.DisconnectAsync();

		Assert.That(await _restore.ApplyStartupAsync(), Is.Null);
	}

	// The settings page lets the action be chosen before the profile is, so this is a half-finished
	// setting rather than a fault — and half-finished settings shouldn't throw at people on startup.
	[Test]
	public async Task Profile_WithNoneChosen_DoesNothing()
	{
		await UseAsync(StartupAction.Profile, profile: null);

		Assert.That(await _restore.ApplyStartupAsync(), Is.Null);
		Assert.That(_keyboard.ColorsAreKnown, Is.False);
	}

	// ── Loading a saved profile ──────────────────────────────────────────────

	[Test]
	public async Task Profile_LandsOnTheBoard()
	{
		await _profiles.SaveAsync("Ember", Green());
		await UseAsync(StartupAction.Profile, "Ember");

		var said = await _restore.ApplyStartupAsync();

		Assert.Multiple(() =>
		{
			Assert.That(ColorOf(DDKey.W), Is.EqualTo(((byte)0, (byte)255, (byte)0)));
			Assert.That(said, Does.Contain("Ember"));
		});
	}

	[Test]
	public async Task Profile_BecomesTheOneInUse()
	{
		await _profiles.SaveAsync("Ember", Green());
		await UseAsync(StartupAction.Profile, "Ember");

		await _restore.ApplyStartupAsync();

		Assert.Multiple(() =>
		{
			Assert.That(_active.Name, Is.EqualTo("Ember"), "so edits have somewhere to be saved back to");
			Assert.That(_active.HasUnsavedChanges, Is.False);
		});
	}

	// Deleting the profile a startup setting points at is easy to do and easy to forget. Saying so is
	// better than opening with settings the user stopped asking for months ago.
	[Test]
	public async Task Profile_ThatHasBeenDeleted_Says_So()
	{
		await UseAsync(StartupAction.Profile, "Ember");

		var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await _restore.ApplyStartupAsync());
		Assert.That(ex!.Message, Does.Contain("Ember"));
	}

	// ── Restoring the last changes ───────────────────────────────────────────

	[Test]
	public async Task LastChanges_WithNothingSaved_DoesNothing()
	{
		await UseAsync(StartupAction.LastChanges);

		Assert.That(await _restore.ApplyStartupAsync(), Is.Null, "the setting was just turned on; there is nothing to say");
		Assert.That(_keyboard.ColorsAreKnown, Is.False);
	}

	[Test]
	public async Task LastChanges_LandOnTheBoard()
	{
		_js.Stored[LastChangesKey] = Green().ToJson();
		await UseAsync(StartupAction.LastChanges);

		var said = await _restore.ApplyStartupAsync();

		Assert.Multiple(() =>
		{
			Assert.That(ColorOf(DDKey.W), Is.EqualTo(((byte)0, (byte)255, (byte)0)));
			Assert.That(said, Is.Not.Null);
		});
	}

	// These changes belong to no saved profile — that is what makes them "last changes" rather than
	// a profile, and offering to update a profile the user never saved would be a lie.
	[Test]
	public async Task LastChanges_AreNotAProfile()
	{
		_js.Stored[LastChangesKey] = Green().ToJson();
		await UseAsync(StartupAction.LastChanges);

		await _restore.ApplyStartupAsync();

		Assert.That(_active.Name, Is.Null);
	}

	// ── Saving as the board changes ──────────────────────────────────────────

	// Longer than SessionRestore's own debounce, so the save it schedules has actually run.
	private static Task SettleAsync() => Task.Delay(1200);

	[Test]
	public async Task LastChanges_AreSavedAsTheBoardChanges()
	{
		await UseAsync(StartupAction.LastChanges);

		await _keyboard.ApplyLightingAsync(
			new RgbColor(0, 255, 0), [DDKey.W], new RgbColor(0, 0, 0), 0, 9);
		await SettleAsync();

		Assert.That(_js.Stored, Does.ContainKey(LastChangesKey));
		var saved = KeyboardProfile.FromJson(_js.Stored[LastChangesKey]);
		Assert.That(saved.Theme, Is.Not.Null, "what was saved should be the board that was written");
	}

	// Selecting a key publishes an actuation preview so the on-screen board can show where the depth
	// is heading. It raises the same change event a real write does, but nothing has been written —
	// and a snapshot taken then holds only the rapid trigger flag the board reported at connect.
	// Storing that would have the next startup announce it had restored settings the user never made.
	[Test]
	public async Task SelectingAKey_IsNotAChangeToSave()
	{
		await UseAsync(StartupAction.LastChanges);

		_keyboard.SetActuationPreview(1.5f);
		await SettleAsync();

		Assert.That(_js.Stored, Does.Not.ContainKey(LastChangesKey), "nothing has been written to the board");
	}

	// The counterpart: rapid trigger is the one setting with no "known" flag on the session, because
	// the board reports it itself. Switching it is still a real change and has to be kept.
	[Test]
	public async Task RapidTrigger_OnItsOwn_IsSaved()
	{
		await UseAsync(StartupAction.LastChanges);

		await _keyboard.SetRapidTriggerAsync(true);
		await SettleAsync();

		Assert.That(_js.Stored, Does.ContainKey(LastChangesKey));
		Assert.That(KeyboardProfile.FromJson(_js.Stored[LastChangesKey]).RapidTrigger, Is.True);
	}

	// The other actions store nothing. Writing a snapshot on every edit for someone who never asked
	// for one is a cost — and a copy of their setup sitting in storage — with no purpose.
	[Test]
	public async Task OtherActions_SaveNothing()
	{
		await UseAsync(StartupAction.Nothing);

		await _keyboard.ApplyLightingAsync(
			new RgbColor(0, 255, 0), [DDKey.W], new RgbColor(0, 0, 0), 0, 9);
		await SettleAsync();

		Assert.That(_js.Stored, Does.Not.ContainKey(LastChangesKey));
	}

	// Turning the setting on with a board already connected should mean this board, now — not
	// whatever it happens to look like after the next edit.
	[Test]
	public async Task SaveNow_StoresTheBoardStraightAway()
	{
		await _restore.SaveNowAsync(Green());

		Assert.That(_js.Stored, Does.ContainKey(LastChangesKey));
		Assert.That(KeyboardProfile.FromJson(_js.Stored[LastChangesKey]).Theme, Is.Not.Null);
	}
}
