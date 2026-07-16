using System.Threading.Channels;
using DrunkDeer.Protocol;
using DrunkDeer.Web.Services;
using Microsoft.JSInterop;

namespace DrunkDeer.Web.Transport;

/// <summary>
/// A keyboard connection over the browser's WebHID API — the transport that lets this app talk
/// to real hardware. Async-only by nature: the browser has one thread, and a blocking read would
/// freeze the page, so this implements <see cref="IKeyboardConnectionAsync"/> and nothing else.
/// Open a session on it with <see cref="KeyboardSession.OpenAsyncConnection"/>.
/// </summary>
/// <remarks>
/// The differences from the desktop transport, all of them forced by WebHID:
/// <list type="bullet">
/// <item>The report-ID byte is not inline. <c>sendReport</c> takes it as a separate argument and
/// input reports arrive with it already removed, so this transport neither prepends nor strips —
/// the opposite of <c>HidTransport</c>, which does both.</item>
/// <item>There is no blocking read to time out. Input reports arrive as events, so they are
/// pushed into a channel here and <see cref="ReceiveCommandAsync"/> waits on that instead.</item>
/// <item>WebHID exposes no serial number, so the desktop's "same physical device" test for
/// binding a separate data stream is impossible. This is command-stream polling only, which is
/// the SDK's normal fallback (and nothing drains the data stream today anyway).</item>
/// </list>
/// </remarks>
public sealed class WebHidKeyboardConnection : IKeyboardConnectionAsync, IAsyncDisposable
{
	// Travel frames arrive at roughly the poll rate and are perishable: if the reader falls
	// behind, the newest frame is the one worth having. Bounded so a stalled reader can't grow
	// this without limit, dropping oldest rather than blocking the browser's event loop.
	private const int InboxCapacity = 256;

	private readonly IJSObjectReference _module;
	private readonly int _handle;
	private readonly int _capacity;
	private readonly Channel<byte[]> _inbox;
	private DotNetObjectReference<WebHidKeyboardConnection>? _self;
	private IdentityResult? _identity;
	private bool _closed;

	/// <summary>The device name the browser reported, for display.</summary>
	public string DeviceName { get; }

	/// <summary>Raised when the browser reports the keyboard was unplugged.</summary>
	public event Action? Disconnected;

	private WebHidKeyboardConnection(IJSObjectReference module, int handle, int capacity, string deviceName)
	{
		_module    = module;
		_handle    = handle;
		_capacity  = capacity;
		DeviceName = deviceName;
		_inbox     = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(InboxCapacity)
		{
			FullMode = BoundedChannelFullMode.DropOldest,
			SingleReader = true,
			SingleWriter = true,
		});
	}

	// Identity is only known once the handshake has run, which needs a live transport — so the
	// object exists before its model does. Reading these earlier is a bug in this class, not
	// something a caller can cause: RequestAsync never hands out a connection un-handshaken.
	private IdentityResult Identity =>
		_identity ?? throw new InvalidOperationException("The identity handshake hasn't completed yet.");

	public ModelInfo Model => Identity.Model;
	public string Variant => Identity.Variant;
	public byte FirmwareVersion => Identity.FirmwareVersion;
	public byte InitialTurboValue => Identity.InitialTurboValue;
	public byte InitialRapidTriggerEnabled => Identity.InitialRapidTriggerEnabled;
	public byte InitialLastWinValue => Identity.InitialLastWinValue;
	public byte InitialRapidTriggerAutoMatch => Identity.InitialRapidTriggerAutoMatch;

	/// <summary>
	/// Always <see langword="false"/>: WebHID gives no serial number, so a second interface can't
	/// be proven to belong to the same physical keyboard. See the note on the class.
	/// </summary>
	public bool HasDataStream => false;

	/// <summary>
	/// Prompts the user to pick a keyboard, opens its command interface, and runs the identity
	/// handshake on it.
	/// </summary>
	/// <remarks>
	/// Must be called from a user gesture: Chrome only shows the device picker with transient
	/// activation, and it will not grant access without the user choosing a device. Keep the
	/// module import off this path (do it at render time) so the gesture isn't spent waiting.
	/// </remarks>
	/// <param name="trace">
	/// Where the handshake's packets are recorded. The handshake has to happen in here, before
	/// anyone outside can hold this connection to wrap it, so tracing is passed in rather than
	/// applied around — and the handshake is the exchange most worth having a record of, since
	/// what the keyboard answers here decides which board the whole app thinks it is talking to.
	/// </param>
	/// <returns>
	/// The open connection, or <see langword="null"/> if the user dismissed the picker without
	/// choosing — that's a normal outcome, not a failure.
	/// </returns>
	/// <exception cref="DrunkDeerDeviceNotFoundException">
	/// A device was chosen, but it never completed the handshake.
	/// </exception>
	public static Task<WebHidKeyboardConnection?> RequestAsync(
		IJSObjectReference module, DiagnosticsLog trace, CancellationToken ct = default) =>
		OpenAsync("requestDevice", module, trace, ct);

	/// <summary>
	/// Opens a keyboard this browser has already been given permission for, without prompting, and
	/// runs the identity handshake on it.
	/// </summary>
	/// <remarks>
	/// Unlike <see cref="RequestAsync"/> this needs no user gesture, because it can only reach a
	/// device the user has already picked from the browser's own permission prompt. That is what
	/// makes reconnecting at startup safe to do unasked.
	/// </remarks>
	/// <returns>
	/// The open connection, or <see langword="null"/> if there is no granted keyboard plugged in —
	/// the ordinary case, and not a failure.
	/// </returns>
	public static Task<WebHidKeyboardConnection?> ReopenKnownAsync(
		IJSObjectReference module, DiagnosticsLog trace, CancellationToken ct = default) =>
		OpenAsync("openKnownDevice", module, trace, ct);

	/// <summary>
	/// The half both ways in share: whichever entry point found the device, what happens to it
	/// afterwards — attach, handshake, or close on the way out — is identical.
	/// </summary>
	private static async Task<WebHidKeyboardConnection?> OpenAsync(
		string entryPoint, IJSObjectReference module, DiagnosticsLog trace, CancellationToken ct)
	{
		var filters = ModelRegistry.DiscoveryPairs
			.Select(p => new { vendorId = p.Vid, productId = p.Pid })
			.ToArray();

		// The [filters] wrapper is load-bearing: InvokeAsync takes params object?[], and an array
		// passed bare would be spread into one JS argument per filter instead of arriving as the
		// single array requestDevice expects.
		var device = await module.InvokeAsync<WebHidDevice?>(entryPoint, ct, [filters]).ConfigureAwait(false);
		if (device is null) return null; // picker dismissed, or nothing already granted

		var connection = new WebHidKeyboardConnection(module, device.Handle, device.Capacity, device.ProductName);
		try
		{
			connection._self = DotNetObjectReference.Create(connection);
			await module.InvokeVoidAsync("attach", ct, connection._handle, connection._self).ConfigureAwait(false);
			connection._identity = await connection.HandshakeAsync(new TracingKeyboardConnection(connection, trace), ct)
				.ConfigureAwait(false);
			return connection;
		}
		catch
		{
			await connection.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// Asks the keyboard who it is, retrying on the SDK's usual budget. A (VID, PID) match is
	/// necessary but not sufficient — the user can pick any device the filters let through — so a
	/// device that never answers is rejected here rather than treated as a keyboard.
	/// </summary>
	/// <param name="io">
	/// The surface to talk to the device through: this connection, wrapped so the exchange can be
	/// observed. Not disposed here — it is a view onto this connection, and disposing it would
	/// close the very device the caller is about to be handed.
	/// </param>
	private async Task<IdentityResult> HandshakeAsync(IKeyboardConnectionAsync io, CancellationToken ct)
	{
		byte[]? lastReceived = null;

		for (int attempt = 0; attempt < IdentityHandshake.Attempts; attempt++)
		{
			await io.FlushReadBufferAsync(ct).ConfigureAwait(false);
			await io.SendAsync(IdentityHandshake.BuildRequest(), ct).ConfigureAwait(false);

			var response = await io.ReceiveCommandAsync(IdentityHandshake.AttemptTimeoutMs, ct).ConfigureAwait(false);
			if (response is null) continue;

			lastReceived = response;
			if (IdentityHandshake.IsComplete(response))
				return IdentityHandshake.Interpret(response);
		}

		throw new DrunkDeerDeviceNotFoundException(
			$"The device you selected never identified itself as a DrunkDeer keyboard " +
			$"({IdentityHandshake.DescribeFailure(lastReceived)}). If it is one, try unplugging and reconnecting it.");
	}

	/// <summary>Sends a pre-built packet with no response expected.</summary>
	public async ValueTask SendAsync(byte[] packet, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ThrowIfClosed();

		// The device's report may be a byte shorter than the SDK's uniform 64-byte packet; the
		// shared rule drops all-zero padding to fit and refuses to drop anything else. Unlike the
		// hidraw path there's no report-ID byte to prepend — send() passes it separately.
		int length = HidReportPacket.FitToCapacity(packet, _capacity, nameof(packet));
		var payload = length == packet.Length ? packet : packet[..length];

		await _module.InvokeVoidAsync("send", cancellationToken, _handle, payload).ConfigureAwait(false);
	}

	/// <summary>Sends a packet and awaits the next response (<see langword="null"/> on timeout).</summary>
	public async ValueTask<byte[]?> SendAndReceiveAsync(
		byte[] packet, int timeoutMs = 1000, CancellationToken cancellationToken = default)
	{
		await SendAsync(packet, cancellationToken).ConfigureAwait(false);
		return await ReceiveCommandAsync(timeoutMs, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Awaits the next input report, or <see langword="null"/> if none arrives within
	/// <paramref name="timeoutMs"/>. A timeout is an ordinary result here — the session's poll
	/// loop expects them — so it isn't raised as an error.
	/// </summary>
	public async ValueTask<byte[]?> ReceiveCommandAsync(
		int timeoutMs = 1000, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (_inbox.Reader.TryRead(out var buffered)) return buffered;
		if (_closed) return null;

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(timeoutMs);
		try
		{
			return await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null; // our timeout, not the caller's cancellation
		}
		catch (ChannelClosedException)
		{
			return null; // disconnected while waiting
		}
	}

	/// <summary>Drops anything buffered from before now, without waiting for new data.</summary>
	public ValueTask FlushReadBufferAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		while (_inbox.Reader.TryRead(out _)) { }
		return ValueTask.CompletedTask;
	}

	/// <summary>Receives one input report from the browser. Not for callers — JS interop only.</summary>
	[JSInvokable]
	public void OnInputReport(byte[] data) => _inbox.Writer.TryWrite(data);

	/// <summary>Reports that the browser saw the keyboard unplugged. Not for callers — JS interop only.</summary>
	[JSInvokable]
	public void OnDisconnected()
	{
		_closed = true;
		_inbox.Writer.TryComplete(); // wake any pending receive instead of stalling it to timeout
		Disconnected?.Invoke();
	}

	private void ThrowIfClosed()
	{
		if (_closed) throw new InvalidOperationException("The keyboard was disconnected.");
	}

	/// <summary>
	/// Closes the device. Prefer <see cref="DisposeAsync"/> — closing is asynchronous in the
	/// browser, so this can only start it and walk away.
	/// </summary>
	/// <remarks>
	/// <see cref="IKeyboardConnectionInfo"/> requires a synchronous Dispose, and that's the one
	/// <see cref="KeyboardSession"/> calls even from its own DisposeAsync. Blocking on the JS call
	/// here would deadlock the single browser thread, so the close is left to run un-awaited: the
	/// page is discarding the device either way, and a failed close on an already-unplugged device
	/// is nothing to act on.
	/// </remarks>
	public void Dispose() => _ = DisposeAsync().AsTask();

	public async ValueTask DisposeAsync()
	{
		if (_closed && _self is null) return;
		_closed = true;
		_inbox.Writer.TryComplete();

		try
		{
			await _module.InvokeVoidAsync("close", _handle).ConfigureAwait(false);
		}
		catch (JSDisconnectedException) { /* the page is going away; nothing to close */ }
		catch (JSException) { /* device already gone */ }

		_self?.Dispose();
		_self = null;
	}

	/// <summary>What the JS side reports back about the interface it opened.</summary>
	private sealed record WebHidDevice(int Handle, int Capacity, string ProductName, int VendorId, int ProductId);
}
