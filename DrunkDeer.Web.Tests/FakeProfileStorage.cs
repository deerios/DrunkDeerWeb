using Microsoft.JSInterop;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Stands in for the browser behind <c>wwwroot/js/profiles.js</c>: the same entry points, backed by
/// a dictionary, so what a test exercises is <see cref="Services.ProfileLibrary"/>'s own logic.
/// </summary>
/// <remarks>
/// It is both the runtime and the module, because that is the shape the library talks through — it
/// imports the module once and then calls it, so handing itself back from the import is all the
/// seam this needs.
/// </remarks>
internal sealed class FakeProfileStorage : IJSRuntime, IJSObjectReference
{
	/// <summary>
	/// What's stored, keyed by profile name. Case-sensitive on purpose: localStorage keys are, so
	/// "ember" and "Ember" are two profiles and a rename between them has real work to do.
	/// </summary>
	public Dictionary<string, string> Stored { get; } = new(StringComparer.Ordinal);

	/// <summary>The calls made, in order. How a test pins that a rename stores the copy before it drops the original.</summary>
	public List<string> Calls { get; } = [];

	/// <summary>When set, <c>write</c> reports this back as a failure rather than storing anything — a full origin.</summary>
	public string? WriteError { get; set; }

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
		ValueTask.FromResult(Dispatch<TValue>(identifier, args));

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) =>
		ValueTask.FromResult(Dispatch<TValue>(identifier, args));

	private TValue Dispatch<TValue>(string identifier, object?[]? args)
	{
		if (identifier == "import") return (TValue)(object)this;

		Calls.Add(identifier);
		object? result = identifier switch
		{
			"list" => Stored.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray(),
			"read" => Stored.TryGetValue(Arg(args, 0), out var json) ? json : null,
			"write" => Write(Arg(args, 0), Arg(args, 1)),
			"remove" => Remove(Arg(args, 0)),
			// Anything else is a call this fake has never heard of, which means the library grew a
			// path the test is silently not covering. Better to say so than to return a default.
			_ => throw new NotSupportedException($"The test reached an unknown module call: {identifier}"),
		};
		return result is null ? default! : (TValue)result;
	}

	// Mirrors the module: null on success, the error's name on failure.
	private string? Write(string name, string json)
	{
		if (WriteError is not null) return WriteError;
		Stored[name] = json;
		return null;
	}

	private object? Remove(string name)
	{
		Stored.Remove(name);
		return null;
	}

	private static string Arg(object?[]? args, int i) =>
		args?[i] as string ?? throw new ArgumentException($"Expected a string argument at {i}.");

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
