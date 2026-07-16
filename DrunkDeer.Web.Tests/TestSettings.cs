using DrunkDeer.Web.Services;

namespace DrunkDeer.Web.Tests;

/// <summary>Settings for tests that need a <see cref="SettingsService"/> but do not care what's in it.</summary>
/// <remarks>
/// <see cref="KeyboardService"/> takes the preferences because test mode lets demo mode be a board
/// other than the A75. A service that has never loaded holds the defaults, so demo mode here
/// connects to the A75 it always did — which is what the tests using this were written against.
/// A test that cares which board it gets should set <see cref="AppSettings.TestModel"/> itself and
/// say so.
/// </remarks>
internal static class TestSettings
{
    public static SettingsService Default() => new(new BrowserStorage(new FakeBrowserStorage()));
}
