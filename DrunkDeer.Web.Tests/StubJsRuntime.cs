using Microsoft.JSInterop;

namespace DrunkDeer.Web.Tests;

/// <summary>
/// Stands in for the browser in tests that drive the services directly.
/// </summary>
/// <remarks>
/// It throws rather than returning a default, because every path exercised here is one that has no
/// business reaching JS. A silent default would let a test pass while quietly proving nothing.
/// </remarks>
internal sealed class StubJsRuntime : IJSRuntime
{
	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
		throw new NotSupportedException($"The test reached JS: {identifier}");

	public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken ct, object?[]? args) =>
		throw new NotSupportedException($"The test reached JS: {identifier}");
}
