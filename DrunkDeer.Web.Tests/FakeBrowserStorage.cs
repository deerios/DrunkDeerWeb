using Microsoft.JSInterop;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Stands in for the browser's localStorage behind <see cref="Services.BrowserStorage"/>: the same
/// three calls it makes, backed by a dictionary.
/// </summary>
/// <remarks>
/// A sibling of <see cref="FakeProfileStorage"/>, and separate from it on purpose — that one fakes
/// the <c>profiles.js</c> module, while this fakes the global <c>localStorage</c> object the settings
/// go through directly. They are different seams and a test usually wants only one of them.
/// </remarks>
internal sealed class FakeBrowserStorage : IJSRuntime
{
	/// <summary>What's stored, by key. Public so a test can seed it, or read what was written.</summary>
	public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

	/// <summary>When set, every call throws it — a browser refusing site data, or a full origin.</summary>
	public Exception? Fault { get; set; }

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
		ValueTask.FromResult(Dispatch<TValue>(identifier, args));

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) =>
		ValueTask.FromResult(Dispatch<TValue>(identifier, args));

	private TValue Dispatch<TValue>(string identifier, object?[]? args)
	{
		if (Fault is not null) throw Fault;

		switch (identifier)
		{
			case "localStorage.getItem":
				return Stored.TryGetValue(Arg(args, 0), out var value) ? (TValue)(object)value : default!;
			case "localStorage.setItem":
				Stored[Arg(args, 0)] = Arg(args, 1);
				return default!;
			case "localStorage.removeItem":
				Stored.Remove(Arg(args, 0));
				return default!;
			// Anything else means the class under test grew a call this fake has never heard of,
			// which the test would otherwise silently not cover.
			default:
				throw new NotSupportedException($"The test reached an unknown JS call: {identifier}");
		}
	}

	private static string Arg(object?[]? args, int i) =>
		args?[i] as string ?? throw new ArgumentException($"Expected a string argument at {i}.");
}
