using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins how the user's preferences survive a page reload — and, more to the point, what happens
/// when they don't.
/// </summary>
[TestFixture]
public class SettingsServiceTests
{
	private FakeBrowserStorage _js = null!;
	private SettingsService _settings = null!;

	[SetUp]
	public void SetUp()
	{
		_js = new FakeBrowserStorage();
		_settings = new SettingsService(new BrowserStorage(_js));
	}

	/// <summary>A second service over the same storage — i.e. the next time the app opens.</summary>
	private async Task<SettingsService> ReloadAsync()
	{
		var reopened = new SettingsService(new BrowserStorage(_js));
		await reopened.LoadAsync();
		return reopened;
	}

	// ── A browser with nothing stored ────────────────────────────────────────

	[Test]
	public async Task NothingStored_LeavesTheDefaults()
	{
		await _settings.LoadAsync();

		Assert.Multiple(() =>
		{
			Assert.That(_settings.Current.AutoConnect, Is.True, "reconnecting to a keyboard you've already allowed is the default");
			Assert.That(_settings.Current.Startup, Is.EqualTo(StartupAction.Nothing), "nothing should reach the board unasked");
			Assert.That(_settings.Current.LiveEditColors, Is.False);
			Assert.That(_settings.Current.SkipWriteConfirmations, Is.False, "a write covers keys you didn't select, so it should be explained until you say otherwise");
			Assert.That(_settings.Current.StartupProfile, Is.Null);
		});
	}

	// ── Round trip ───────────────────────────────────────────────────────────

	[Test]
	public async Task Saved_ComesBackOnTheNextLoad()
	{
		await _settings.SaveAsync(new AppSettings
		{
			LiveEditColors = true,
			SkipWriteConfirmations = true,
			AutoConnect = false,
			Startup = StartupAction.Profile,
			StartupProfile = "Ember",
		});

		var reopened = await ReloadAsync();

		Assert.Multiple(() =>
		{
			Assert.That(reopened.Current.LiveEditColors, Is.True);
			Assert.That(reopened.Current.SkipWriteConfirmations, Is.True);
			Assert.That(reopened.Current.AutoConnect, Is.False);
			Assert.That(reopened.Current.Startup, Is.EqualTo(StartupAction.Profile));
			Assert.That(reopened.Current.StartupProfile, Is.EqualTo("Ember"));
		});
	}

	// The reason the converter is there: a number would re-point an existing choice at a different
	// action the moment the enum gains a member anywhere but the end.
	[Test]
	public async Task StartupAction_IsStoredByName()
	{
		await _settings.SaveAsync(new AppSettings { Startup = StartupAction.LastChanges });

		Assert.That(_js.Stored.Values.Single(), Does.Contain("LastChanges"));
	}

	[Test]
	public async Task Save_PublishesTheChange()
	{
		await _settings.LoadAsync();
		int changes = 0;
		_settings.Changed += () => changes++;

		await _settings.SaveAsync(new AppSettings { LiveEditColors = true });

		Assert.Multiple(() =>
		{
			Assert.That(changes, Is.EqualTo(1));
			Assert.That(_settings.Current.LiveEditColors, Is.True);
		});
	}

	// The settings page goes on holding the object it saved, bound to live form controls, so the
	// copy the app reads has to be its own.
	[Test]
	public async Task Save_TakesACopy()
	{
		var edited = new AppSettings { LiveEditColors = true };
		await _settings.SaveAsync(edited);

		edited.LiveEditColors = false;

		Assert.That(_settings.Current.LiveEditColors, Is.True, "the saved settings shouldn't change under the app");
	}

	// ── Storage that can't be trusted ────────────────────────────────────────

	[Test]
	public async Task UnreadableJson_FallsBackToTheDefaults()
	{
		_js.Stored["drunkdeer.settings"] = "{ this is not json";

		await _settings.LoadAsync();

		Assert.That(_settings.Current.AutoConnect, Is.True, "the defaults are a working app; refusing to start isn't");
	}

	[Test]
	public async Task StoredNull_FallsBackToTheDefaults()
	{
		_js.Stored["drunkdeer.settings"] = "null";

		await _settings.LoadAsync();

		Assert.That(_settings.Current, Is.Not.Null);
		Assert.That(_settings.Current.AutoConnect, Is.True);
	}

	// A browser set to refuse site data throws on the way in and on the way out. Neither is worth
	// interrupting someone over: what's lost is a preference, and the app works without it.
	[Test]
	public async Task StorageThatRefuses_IsNotAnError()
	{
		_js.Fault = new InvalidOperationException("JavaScript interop calls cannot be issued.");

		await _settings.LoadAsync();
		await _settings.SaveAsync(new AppSettings { LiveEditColors = true });

		Assert.That(_settings.Current.LiveEditColors, Is.True, "the setting still applies to this session");
	}
}
