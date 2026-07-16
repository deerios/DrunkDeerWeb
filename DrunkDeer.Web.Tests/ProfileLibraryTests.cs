using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Pins how <see cref="ProfileLibrary"/> moves profiles around the browser's storage — in
/// particular that a rename cannot lose one.
/// </summary>
[TestFixture]
public class ProfileLibraryTests
{
	private FakeProfileStorage _storage = null!;
	private ProfileLibrary _library = null!;

	[SetUp]
	public void SetUp()
	{
		_storage = new FakeProfileStorage();
		_library = new ProfileLibrary(_storage);
	}

	// A profile with something in it, so a rename that moved the name but not the contents would
	// show up rather than passing on two empty strings being equal.
	private static KeyboardProfile Profile(float depth = 1.5f) =>
		new() { Actuation = new KeyDepthProfile { Default = depth } };

	// ── The ordinary rename ─────────────────────────────────────────────────

	[Test]
	public async Task Rename_MovesTheProfileToTheNewName()
	{
		await _library.SaveAsync("Ember", Profile());

		Assert.That(await _library.RenameAsync("Ember", "Cinder"), Is.True);

		Assert.That(await _library.ListAsync(), Is.EqualTo(new[] { "Cinder" }));
		var moved = await _library.LoadAsync("Cinder");
		Assert.That(moved?.Actuation?.Default, Is.EqualTo(1.5f), "the contents should travel with the name");
	}

	[Test]
	public async Task Rename_OverAnExistingProfile_ReplacesIt()
	{
		await _library.SaveAsync("Ember", Profile(1.5f));
		await _library.SaveAsync("Cinder", Profile(3.0f));

		await _library.RenameAsync("Ember", "Cinder");

		Assert.That(await _library.ListAsync(), Is.EqualTo(new[] { "Cinder" }));
		var kept = await _library.LoadAsync("Cinder");
		Assert.That(kept?.Actuation?.Default, Is.EqualTo(1.5f), "the renamed profile should win");
	}

	[Test]
	public async Task Rename_ChangingOnlyTheCase_IsARealRename()
	{
		await _library.SaveAsync("ember", Profile());

		Assert.That(await _library.RenameAsync("ember", "Ember"), Is.True);

		// The point: localStorage keys are case-sensitive, so an ignore-case "nothing changed" check
		// would leave the profile filed under the old name and report success.
		Assert.That(_storage.Stored.Keys, Is.EqualTo(new[] { "Ember" }));
	}

	[Test]
	public async Task Rename_ToItsOwnName_TouchesNothing()
	{
		await _library.SaveAsync("Ember", Profile());
		_storage.Calls.Clear();

		Assert.That(await _library.RenameAsync("Ember", "Ember"), Is.True);

		Assert.That(_storage.Calls, Is.Empty, "renaming a profile to what it is already called is not work");
		Assert.That(_storage.Stored, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task Rename_OfAProfileThatIsGone_ReportsIt()
	{
		Assert.That(await _library.RenameAsync("Ember", "Cinder"), Is.False);
		Assert.That(_storage.Stored, Is.Empty, "nothing to move means nothing to write");
	}

	[TestCase("")]
	[TestCase("has spaces")]
	[TestCase("parens(1)")]
	public void Rename_ToAnUnusableName_Throws(string bad)
	{
		Assert.ThrowsAsync<ArgumentException>(async () => await _library.RenameAsync("Ember", bad));
	}

	// ── That it cannot lose a profile ────────────────────────────────────────

	[Test]
	public async Task Rename_StoresTheCopyBeforeDroppingTheOriginal()
	{
		await _library.SaveAsync("Ember", Profile());
		_storage.Calls.Clear();

		await _library.RenameAsync("Ember", "Cinder");

		// Ordering, not just the end state: a remove that ran first would look identical here on a
		// good day, and lose the profile on the day the write fails.
		Assert.That(_storage.Calls, Is.EqualTo(new[] { "read", "write", "remove" }));
	}

	[Test]
	public async Task Rename_ThatTheBrowserRefuses_LeavesTheProfileWhereItWas()
	{
		await _library.SaveAsync("Ember", Profile());
		_storage.WriteError = "QuotaExceededError";

		Assert.ThrowsAsync<InvalidOperationException>(async () => await _library.RenameAsync("Ember", "Cinder"));

		Assert.That(await _library.ListAsync(), Is.EqualTo(new[] { "Ember" }),
			"a rename that couldn't store the copy must not have dropped the original");
		Assert.That(_storage.Calls, Does.Not.Contain("remove"));
	}
}
