using System.Text.RegularExpressions;
using DrunkDeer.Protocol;
using Microsoft.JSInterop;

namespace DrunkDeer.Web.Services;

/// <summary>
/// The browser-side store of named <see cref="KeyboardProfile"/>s, kept in localStorage, plus
/// export to and import from a JSON file.
/// </summary>
/// <remarks>
/// The file format is the SDK's own <see cref="KeyboardProfile"/> JSON, which is exactly what the
/// <c>deerkb</c> CLI reads and writes — so a profile saved here can be dropped into the CLI's
/// profile directory, and vice versa, without a conversion step.
/// </remarks>
public sealed partial class ProfileLibrary : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public ProfileLibrary(IJSRuntime js) => _js = js;

    private async ValueTask<IJSObjectReference> ModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/profiles.js").ConfigureAwait(false);

    // Matches the CLI's rule (DrunkDeer.Cli ProfileStore), so a name that works in one tool works
    // in the other. localStorage needs no such restriction itself — the CLI does, because there a
    // name becomes a file path.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    /// <summary>Whether <paramref name="name"/> is usable as a profile name.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && NamePattern().IsMatch(name);

    /// <summary>Why <paramref name="name"/> is unusable, or <see langword="null"/> if it's fine.</summary>
    public static string? DescribeNameProblem(string? name) =>
        IsValidName(name)
            ? null
            : "Use 1–64 characters: letters, digits, '-' or '_' only.";

    /// <summary>Names of every saved profile, sorted case-insensitively.</summary>
    public async Task<IReadOnlyList<string>> ListAsync()
    {
        var module = await ModuleAsync().ConfigureAwait(false);
        return await module.InvokeAsync<string[]>("list").ConfigureAwait(false);
    }

    /// <summary>Saves <paramref name="profile"/> under <paramref name="name"/>, replacing any existing one.</summary>
    /// <exception cref="InvalidOperationException">The browser refused to store it (usually a full origin).</exception>
    public async Task SaveAsync(string name, KeyboardProfile profile)
    {
        Validate(name);
        var module = await ModuleAsync().ConfigureAwait(false);
        var error = await module.InvokeAsync<string?>("write", name, profile.ToJson()).ConfigureAwait(false);
        if (error is not null)
            throw new InvalidOperationException($"The browser wouldn't save this profile ({error}).");
    }

    /// <summary>Loads the profile saved as <paramref name="name"/>, or null if there isn't one.</summary>
    /// <exception cref="InvalidOperationException">The stored JSON is not a readable profile.</exception>
    public async Task<KeyboardProfile?> LoadAsync(string name)
    {
        Validate(name);
        var module = await ModuleAsync().ConfigureAwait(false);
        var json = await module.InvokeAsync<string?>("read", name).ConfigureAwait(false);
        return json is null ? null : Parse(json, $"The saved profile '{name}'");
    }

    /// <summary>
    /// Renames the profile saved as <paramref name="from"/> to <paramref name="to"/>, replacing
    /// anything already saved under the new name.
    /// </summary>
    /// <returns><see langword="false"/> if there was nothing saved as <paramref name="from"/>.</returns>
    /// <exception cref="InvalidOperationException">The browser refused to store it (usually a full origin).</exception>
    public async Task<bool> RenameAsync(string from, string to)
    {
        Validate(from);
        Validate(to);
        // Not an ignore-case comparison: names differing only in case are different profiles here
        // (localStorage keys are case-sensitive), so "ember" -> "Ember" is a real rename and has to
        // do the work. Only a rename to the identical name is the no-op.
        if (string.Equals(from, to, StringComparison.Ordinal)) return true;

        var module = await ModuleAsync().ConfigureAwait(false);
        var json = await module.InvokeAsync<string?>("read", from).ConfigureAwait(false);
        if (json is null) return false;

        // Written before the old one is dropped, so a rename that fails half way — a full origin is
        // the realistic way — leaves the profile where it was rather than losing it.
        var error = await module.InvokeAsync<string?>("write", to, json).ConfigureAwait(false);
        if (error is not null)
            throw new InvalidOperationException($"The browser wouldn't save this profile ({error}).");

        await module.InvokeVoidAsync("remove", from).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deletes the profile saved as <paramref name="name"/>. Deleting a missing one is not an error.</summary>
    public async Task DeleteAsync(string name)
    {
        Validate(name);
        var module = await ModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("remove", name).ConfigureAwait(false);
    }

    /// <summary>Offers <paramref name="profile"/> to the user as a downloaded <c>.json</c> file.</summary>
    public async Task ExportAsync(string name, KeyboardProfile profile)
    {
        var module = await ModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("download", $"{name}.json", profile.ToJson()).ConfigureAwait(false);
    }

    /// <summary>Reads a profile out of text the user supplied from a file.</summary>
    /// <exception cref="InvalidOperationException">The text is not a readable profile.</exception>
    public static KeyboardProfile Import(string json) => Parse(json, "That file");

    // KeyboardProfile.FromJson throws whatever System.Text.Json throws, whose messages talk about
    // line numbers and token types. The user picked a file; tell them about the file.
    private static KeyboardProfile Parse(string json, string subject)
    {
        try
        {
            return KeyboardProfile.FromJson(json);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException)
        {
            throw new InvalidOperationException($"{subject} isn't a keyboard profile the app can read.", ex);
        }
    }

    private static void Validate(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException(DescribeNameProblem(name), nameof(name));
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;
        try { await _module.DisposeAsync().ConfigureAwait(false); }
        catch (JSDisconnectedException) { /* the page is already gone */ }
        _module = null;
    }
}
