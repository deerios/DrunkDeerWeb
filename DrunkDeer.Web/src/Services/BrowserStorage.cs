using Microsoft.JSInterop;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Reads and writes single values in the browser's localStorage.
/// </summary>
/// <remarks>
/// Deliberately not a JS module of its own, unlike <see cref="ProfileLibrary"/>: localStorage is
/// already callable straight from interop, and a module would exist only to forward to it. Profiles
/// have a module because they need to enumerate keys by prefix and offer a file download, neither of
/// which localStorage does in one call.
/// <para>
/// Nothing here reports a failed write. Storage can be full, or blocked outright when the browser is
/// set to refuse site data, and neither is worth interrupting someone over: what is stored through
/// here is a setting and a scratch snapshot, and losing one means the app opens with its defaults.
/// A profile is different — that is the user's own work, and <see cref="ProfileLibrary"/> throws
/// rather than lose one quietly.
/// </para>
/// </remarks>
public sealed class BrowserStorage
{
    private readonly IJSRuntime _js;

    public BrowserStorage(IJSRuntime js) => _js = js;

    /// <summary>What's stored under <paramref name="key"/>, or null if nothing is (or storage is unreadable).</summary>
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            return await _js.InvokeAsync<string?>("localStorage.getItem", key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>. Silently does nothing if it can't.</summary>
    public async Task SetAsync(string key, string value)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", key, value).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
            // See the note on the class: nothing stored through here is worth a message.
        }
    }

    /// <summary>Removes whatever is stored under <paramref name="key"/>.</summary>
    public async Task RemoveAsync(string key)
    {
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", key).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }
}
