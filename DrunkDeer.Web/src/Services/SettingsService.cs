using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrunkDeer.Web.Services;

/// <summary>
/// The one copy of the user's <see cref="AppSettings"/>, loaded once at startup and written back
/// whenever they change.
/// </summary>
/// <remarks>
/// <see cref="Current"/> is readable synchronously and never null, which is what lets a component
/// consult a setting mid-render. That is only true because <see cref="LoadAsync"/> runs before the
/// app does — see <c>Program.cs</c>.
/// </remarks>
public sealed class SettingsService
{
    private const string StorageKey = "drunkdeer.settings";

    private static readonly JsonSerializerOptions Format = new()
    {
        // Written as names, so what's in storage survives the enum gaining a member in the middle.
        // Numbers would silently re-point an existing choice at a different action.
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly BrowserStorage _storage;

    public SettingsService(BrowserStorage storage) => _storage = storage;

    /// <summary>The settings in force. Never null: a browser with nothing stored gets the defaults.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after <see cref="SaveAsync"/> replaces <see cref="Current"/>.</summary>
    public event Action? Changed;

    /// <summary>Reads the stored settings. Call once, before the first render.</summary>
    public async Task LoadAsync()
    {
        var json = await _storage.GetAsync(StorageKey).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return;

        try
        {
            // A null deserialises from the literal "null", which is not a settings object.
            Current = JsonSerializer.Deserialize<AppSettings>(json, Format) ?? new AppSettings();
        }
        catch (JsonException)
        {
            // Storage was hand-edited, or written by a version whose shape no longer parses. The
            // defaults are a working app; refusing to start over a preferences file is not.
            Current = new AppSettings();
        }
    }

    /// <summary>Makes <paramref name="settings"/> the settings in force and stores them.</summary>
    public async Task SaveAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Cloned on the way in as well as out: the caller is usually the settings page, which goes
        // on holding its copy bound to live form controls after saving.
        Current = settings.Clone();
        Changed?.Invoke();
        await _storage.SetAsync(StorageKey, JsonSerializer.Serialize(Current, Format)).ConfigureAwait(false);
    }
}
