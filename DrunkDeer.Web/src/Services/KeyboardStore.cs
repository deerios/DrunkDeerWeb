namespace DrunkDeer.Web.Services;

/// <summary>Lifecycle state of the one keyboard connection this app owns.</summary>
public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Faulted,
}

/// <summary>
/// The UI-facing snapshot of connection state. Components subscribe to <see cref="Changed"/>
/// and read these properties; they never touch the session directly. This is the "structural"
/// state (connect / disconnect / model identity) — it changes rarely, so a plain event →
/// <c>StateHasChanged</c> is fine. Fast-changing per-key travel is read live off the session
/// by the keyboard view, not funnelled through here (see FUTURE_PLAN §5.4).
/// </summary>
public sealed class KeyboardStore
{
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public string? ModelName { get; private set; }
    public string? Variant { get; private set; }
    public byte FirmwareVersion { get; private set; }
    public bool IsDemo { get; private set; }

    /// <summary><see langword="true"/> unless the connected model is the A75 — the only human-verified board.</summary>
    public bool IsUnverifiedModel { get; private set; }

    /// <summary>Human-readable detail for the current status (e.g. an error message, "Demo mode").</summary>
    public string? StatusMessage { get; private set; }

    public bool IsConnected => Status == ConnectionStatus.Connected;

    /// <summary>Raised on any structural change. Handlers must marshal to the UI thread themselves.</summary>
    public event Action? Changed;

    public void SetConnecting(bool demo)
    {
        Status = ConnectionStatus.Connecting;
        IsDemo = demo;
        StatusMessage = demo ? "Starting demo mode…" : "Connecting…";
        NotifyChanged();
    }

    public void SetConnected(string modelName, string variant, byte firmware, bool demo, bool unverified)
    {
        Status = ConnectionStatus.Connected;
        ModelName = modelName;
        Variant = variant;
        FirmwareVersion = firmware;
        IsDemo = demo;
        IsUnverifiedModel = unverified;
        StatusMessage = demo ? "Demo mode (simulated keyboard)" : null;
        NotifyChanged();
    }

    public void SetDisconnected(string? message = null)
    {
        Status = ConnectionStatus.Disconnected;
        ModelName = null;
        Variant = null;
        FirmwareVersion = 0;
        IsDemo = false;
        IsUnverifiedModel = false;
        StatusMessage = message;
        NotifyChanged();
    }

    public void SetFaulted(string message)
    {
        Status = ConnectionStatus.Faulted;
        StatusMessage = message;
        NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();
}
