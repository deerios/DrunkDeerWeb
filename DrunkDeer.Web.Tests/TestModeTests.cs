using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Coverage for test mode: the boards demo mode can be told to pretend to be, and what the
/// simulated session actually comes back as when it is told.
/// </summary>
[TestFixture]
public class TestModeTests
{
    private KeyboardService _service = null!;

    private static async Task<SettingsService> SettingsForAsync(string? slug, string? variant)
    {
        var prefs = new SettingsService(new BrowserStorage(new FakeBrowserStorage()));
        await prefs.SaveAsync(new AppSettings { TestModel = slug, TestVariant = variant });
        return prefs;
    }

    private async Task<KeyboardSession> ConnectAsync(string? slug, string? variant)
    {
        _service = new KeyboardService(
            new KeyboardStore(), new StubJsRuntime(), new DiagnosticsLog(), NullLoggerFactory.Instance,
            await SettingsForAsync(slug, variant));
        await _service.ConnectDemoAsync();
        return _service.Session!;
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_service is not null) await _service.DisposeAsync();
    }

    /// <summary>
    /// The board list repeats the SDK's private identity table, so this is what stops it drifting
    /// into naming a model that no longer exists — the failure it is guarding against is a picker
    /// entry that silently falls back to the A75 and looks like the layout is wrong.
    /// </summary>
    [Test]
    public void TestBoardsAreRealModels()
    {
        Assert.Multiple(() =>
        {
            foreach (var board in TestBoards.All)
                Assert.That(ModelRegistry.GetInfo(board.Slug), Is.Not.Null,
                    $"Test board '{board.Name}' names a slug the registry does not have: {board.Slug}.");
        });
    }

    /// <summary>Each entry must be a real identity, not just a real slug with a variant made up.</summary>
    [Test]
    public void EveryTestBoard_IsAResolvableIdentity()
    {
        // Resolve() goes the other way (bytes -> slug), so the identity table is reached by asking
        // it about every byte triple it knows. Anything the app offers must be one of the answers.
        var known = new HashSet<(string, string)>();
        for (int b0 = 0; b0 <= 0x20; b0++)
        for (int b1 = 0; b1 <= 0x20; b1++)
        for (int b2 = 0; b2 <= 0x20; b2++)
            if (ModelRegistry.Resolve((byte)b0, (byte)b1, (byte)b2) is { } hit)
                known.Add(hit);

        Assert.Multiple(() =>
        {
            foreach (var board in TestBoards.All)
                Assert.That(known, Does.Contain((board.Slug, board.Variant)),
                    $"Test board '{board.Name}' is not a (slug, variant) any real keyboard reports.");
        });
    }

    [Test]
    public void TestBoards_HaveNoDuplicates()
    {
        var keys = TestBoards.All.Select(b => (b.Slug, b.Variant)).ToList();
        Assert.That(keys.Distinct().Count(), Is.EqualTo(keys.Count), "The same board is offered twice.");
    }

    [Test]
    public async Task NoTestModel_ConnectsToTheA75()
    {
        // The default has to stay what demo mode was before test mode existed.
        var session = await ConnectAsync(null, null);
        Assert.That(session.Model.Slug, Is.EqualTo(ModelSlugs.A75));
        Assert.That(session.Variant, Is.EqualTo("ansi"));
    }

    [Test]
    public async Task ATestModel_IsWhatDemoModeReports()
    {
        var session = await ConnectAsync(ModelSlugs.G65, "ansi");
        Assert.That(session.Model.Slug, Is.EqualTo(ModelSlugs.G65));
    }

    /// <summary>
    /// The point of the feature: the faked identity has to produce that model's real geometry, not
    /// the A75's. A test mode that reported G65 and drew an A75 would be worse than none.
    /// </summary>
    [Test]
    public async Task ATestModel_DrawsThatModelsOwnBoard()
    {
        var session = await ConnectAsync(ModelSlugs.G65, "ansi");
        Assert.That(session.HasLayout, Is.True);
        Assert.That(session.Layout, Has.Count.EqualTo(68), "the G65 is a 68-key board");
        Assert.That(session.BoardWidth, Is.EqualTo(16f).Within(0.001f));
        Assert.That(session.BoardHeight, Is.EqualTo(5f).Within(0.001f));
    }

    [Test]
    public async Task TheG60_DrawsNarrowerThanTheG65()
    {
        // Two models that share a shape but not a size: it catches a fallback that returns some
        // other board's geometry while still reporting the right slug.
        var session = await ConnectAsync(ModelSlugs.G60, "ansi");
        Assert.That(session.Layout, Has.Count.EqualTo(61));
        Assert.That(session.BoardWidth, Is.EqualTo(15f).Within(0.001f));
    }

    [Test]
    public async Task TheA75Ultra_SharesTheA75sBoard()
    {
        var session = await ConnectAsync(ModelSlugs.A75Ultra, "ansi");
        Assert.That(session.Model.Slug, Is.EqualTo(ModelSlugs.A75Ultra));
        Assert.That(session.Layout, Has.Count.EqualTo(82), "the A75 Ultra is the same board as the A75");
    }

    /// <summary>A board with no geometry connects and says so, rather than failing to connect.</summary>
    [Test]
    public async Task AModelWithNoGeometry_StillConnects()
    {
        var session = await ConnectAsync(ModelSlugs.A75, "iso");
        Assert.That(session.HasLayout, Is.False);
        Assert.That(session.Layout, Is.Empty);
    }

    /// <summary>
    /// The setting outlives the app: a slug stored by an older version, or hand-edited, must not
    /// leave someone unable to open demo mode at all.
    /// </summary>
    [Test]
    public async Task AnUnknownTestModel_FallsBackToTheA75()
    {
        var session = await ConnectAsync("no_such_keyboard", "ansi");
        Assert.That(session.Model.Slug, Is.EqualTo(ModelSlugs.A75));
        Assert.That(session.Variant, Is.EqualTo("ansi"), "a stale variant must not survive the fallback either");
        Assert.That(session.HasLayout, Is.True);
    }

    [Test]
    public void HasGeometry_FollowsTheShippedData()
    {
        // Asked of the SDK rather than recorded in the list, so these track the geometry as it lands.
        Assert.Multiple(() =>
        {
            Assert.That(TestBoards.Find(ModelSlugs.G65, "ansi")!.HasGeometry, Is.True);
            Assert.That(TestBoards.Find(ModelSlugs.A75, "iso")!.HasGeometry, Is.False);
        });
    }
}
