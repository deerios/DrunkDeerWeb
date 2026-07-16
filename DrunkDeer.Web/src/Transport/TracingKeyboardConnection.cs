using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;

namespace DrunkDeer.Web.Transport;

/// <summary>
/// Wraps a keyboard connection and reports everything crossing it to a <see cref="DiagnosticsLog"/>.
/// </summary>
/// <remarks>
/// Sits between the session and the real transport so both the simulated keyboard and a WebHID one
/// are traced by the same code — the diagnostics page then shows demo traffic that is honestly the
/// same shape as hardware traffic, which is what makes it worth practising on.
/// <para>
/// Identity is proxied rather than copied: the handshake fills it in on the connection underneath
/// after this wrapper is constructed, so reading it once here would capture a blank model.
/// </para>
/// </remarks>
public sealed class TracingKeyboardConnection : IKeyboardConnectionAsync, IAsyncDisposable
{
    private readonly IKeyboardConnectionAsync _inner;
    private readonly DiagnosticsLog _log;

    // What the last packet out was tells a bare read what it is waiting for; see ReceiveCommandAsync.
    private bool _lastSendWasPolling;

    public TracingKeyboardConnection(IKeyboardConnectionAsync inner, DiagnosticsLog log)
    {
        _inner = inner;
        _log = log;
    }

    /// <summary>The connection being traced.</summary>
    public IKeyboardConnectionAsync Inner => _inner;

    public ModelInfo Model => _inner.Model;
    public string Variant => _inner.Variant;
    public byte FirmwareVersion => _inner.FirmwareVersion;
    public bool HasDataStream => _inner.HasDataStream;
    public byte InitialTurboValue => _inner.InitialTurboValue;
    public byte InitialRapidTriggerEnabled => _inner.InitialRapidTriggerEnabled;
    public byte InitialLastWinValue => _inner.InitialLastWinValue;
    public byte InitialRapidTriggerAutoMatch => _inner.InitialRapidTriggerAutoMatch;

    public async ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken = default)
    {
        // Recorded before the send so a packet that throws still appears — that packet is the one
        // worth seeing.
        _lastSendWasPolling = _log.RecordSent(packet);
        await _inner.SendAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<byte[]?> SendAndReceiveAsync(
        byte[] packet, int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        _lastSendWasPolling = _log.RecordSent(packet);
        var response = await _inner.SendAndReceiveAsync(packet, timeoutMs, cancellationToken).ConfigureAwait(false);
        Record(response, _lastSendWasPolling);
        return response;
    }

    public async ValueTask<byte[]?> ReceiveCommandAsync(
        int timeoutMs = 1000, CancellationToken cancellationToken = default)
    {
        var response = await _inner.ReceiveCommandAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
        Record(response, _lastSendWasPolling);
        return response;
    }

    // A read on its own can't say what it was waiting for, which matters only when nothing comes
    // back: a timeout has no bytes to classify, and whether it's worth showing depends entirely on
    // what was expected. The last packet out answers that — the session serialises each exchange
    // on its wire gate, so a read always belongs to the send before it. It keeps an unplugged board
    // from burying the timeline under poll timeouts, while a handshake that goes unanswered — the
    // one timeout somebody really does need to see — still shows up.
    private void Record(byte[]? response, bool whilePolling)
    {
        if (response is null) _log.RecordTimeout(whilePolling);
        else _log.RecordReceived(response);
    }

    public ValueTask FlushReadBufferAsync(CancellationToken cancellationToken = default) =>
        _inner.FlushReadBufferAsync(cancellationToken);

    public void Dispose() => _inner.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_inner is IAsyncDisposable async) await async.DisposeAsync().ConfigureAwait(false);
        else _inner.Dispose();
    }
}
