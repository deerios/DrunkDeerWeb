using System.Diagnostics;
using DrunkDeer.Protocol;

namespace DrunkDeer.Web.Services;

/// <summary>What produced a timeline entry.</summary>
public enum TraceKind
{
    /// <summary>A packet the app sent to the keyboard.</summary>
    Sent,

    /// <summary>A packet the keyboard sent back.</summary>
    Received,

    /// <summary>A read that expired before the keyboard answered.</summary>
    Timeout,

    /// <summary>A line the SDK itself logged, rather than anything on the wire.</summary>
    Note,
}

/// <summary>How much of the traffic is kept in the timeline.</summary>
public enum TraceCapture
{
    /// <summary>Record nothing new; the counters keep running.</summary>
    Paused,

    /// <summary>Skip the travel polling and keep everything else.</summary>
    ConfigOnly,

    /// <summary>Record every packet, polling included.</summary>
    Everything,
}

/// <summary>One entry in the diagnostics timeline.</summary>
/// <param name="Sequence">Monotonic counter, so entries are still ordered when two share a timestamp.</param>
/// <param name="At">Time since the log started, which is what a wire trace is actually read against.</param>
/// <param name="Payload">The packet bytes, or <see langword="null"/> for entries that aren't packets.</param>
/// <param name="Message">The text of a <see cref="TraceKind.Note"/>, otherwise <see langword="null"/>.</param>
public sealed record TraceEntry(
    long Sequence,
    TimeSpan At,
    TraceKind Kind,
    byte[]? Payload,
    string? Message)
{
    /// <summary>The direction this packet crossed the wire, or <see langword="null"/> if it isn't a packet.</summary>
    public PacketDirection? Direction => Kind switch
    {
        TraceKind.Sent => PacketDirection.HostToDevice,
        TraceKind.Received => PacketDirection.DeviceToHost,
        _ => null,
    };
}

/// <summary>
/// A bounded, always-on record of what crossed the wire, feeding the diagnostics page.
/// </summary>
/// <remarks>
/// Two things are separated here on purpose, because the traffic is wildly lopsided: a connected
/// keyboard is polled for key travel hundreds of times a second, while everything a user actually
/// does amounts to a handful of packets.
/// <list type="bullet">
/// <item>The <b>counters</b> are always running and never allocate, so they can be fed from the
/// poll loop without becoming part of what makes it slow.</item>
/// <item>The <b>timeline</b> defaults to skipping the polling entirely. Not for the cost - for the
/// signal: at full rate the poll would evict a configuration write out of the ring within a
/// second of it happening, which is precisely the packet someone opened this page to find.</item>
/// </list>
/// Entries hold raw bytes and are named by <see cref="ProtocolOracle"/> only when they're rendered.
/// A packet costs one array copy to record and nothing to ignore; classifying at capture time
/// would mean classifying hundreds a second to display a few dozen.
/// <para>
/// No change event: at poll rate an event per packet would be an invitation to re-render the page
/// hundreds of times a second, which is the mistake the whole keyboard view was built to avoid.
/// The page reads this on its own slow timer instead.
/// </para>
/// </remarks>
public sealed class DiagnosticsLog
{
    // ~1.5 s of unfiltered polling, or a long history of real traffic. Big enough that a user
    // watching "Everything" sees a burst in context; small enough to snapshot cheaply.
    private const int Capacity = 512;

    private readonly TraceEntry[] _ring = new TraceEntry[Capacity];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private int _next;
    private int _count;
    private long _sequence;

    // Rolling poll-rate window. A TimeSpan, not raw Stopwatch ticks: those are counted in
    // Stopwatch.Frequency units, which is not TimeSpan's 100 ns and is not the same number twice
    // across platforms.
    private TimeSpan _pollBucketStart;
    private int _pollsThisBucket;

    /// <summary>How much of the traffic is being recorded. Changing it never clears what's there.</summary>
    public TraceCapture Capture { get; set; } = TraceCapture.ConfigOnly;

    public int PacketsSent { get; private set; }
    public int PacketsReceived { get; private set; }
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Reads that expired before the keyboard answered. Routine in small numbers; a climbing count is not.</summary>
    public int Timeouts { get; private set; }

    /// <summary>
    /// Travel requests per second, over the last full second. This is how fast the app is asking
    /// for key travel — not a promise the keyboard answered that often. Dropped frames say that.
    /// </summary>
    public double PollRateHz { get; private set; }

    /// <summary>Entries dropped from the timeline because the ring wrapped.</summary>
    public long Evicted { get; private set; }

    /// <summary>Time since the log started counting.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Records a packet on its way to the keyboard.</summary>
    /// <returns>Whether this packet was part of the travel polling, which the caller would otherwise have to ask twice.</returns>
    public bool RecordSent(ReadOnlySpan<byte> packet)
    {
        bool polling = ProtocolOracle.IsTravelPolling(packet, PacketDirection.HostToDevice);

        lock (_gate)
        {
            PacketsSent++;
            BytesSent += packet.Length;
            if (polling) CountPoll();
            if (ShouldCapture(polling)) Append(TraceKind.Sent, packet.ToArray(), null);
        }

        return polling;
    }

    public void RecordReceived(ReadOnlySpan<byte> packet)
    {
        bool polling = ProtocolOracle.IsTravelPolling(packet, PacketDirection.DeviceToHost);

        lock (_gate)
        {
            PacketsReceived++;
            BytesReceived += packet.Length;
            if (ShouldCapture(polling)) Append(TraceKind.Received, packet.ToArray(), null);
        }
    }

    /// <summary>
    /// Records a read that expired. Always counted, but only shown in the timeline when the
    /// polling is — a timeout during polling is the poll loop's own business, and at poll rate
    /// a disconnected board would otherwise fill the timeline with them.
    /// </summary>
    public void RecordTimeout(bool whilePolling)
    {
        lock (_gate)
        {
            Timeouts++;
            if (ShouldCapture(whilePolling))
                Append(TraceKind.Timeout, null, "Read timed out");
        }
    }

    /// <summary>Records a line the SDK logged. Never treated as polling: the SDK doesn't log per frame above Trace.</summary>
    public void RecordNote(string message)
    {
        lock (_gate)
        {
            if (Capture == TraceCapture.Paused) return;
            Append(TraceKind.Note, null, message);
        }
    }

    private bool ShouldCapture(bool polling) => Capture switch
    {
        TraceCapture.Everything => true,
        TraceCapture.ConfigOnly => !polling,
        _ => false,
    };

    // Caller holds the lock.
    private void CountPoll()
    {
        var now = _clock.Elapsed;
        _pollsThisBucket++;

        var span = now - _pollBucketStart;
        if (span < TimeSpan.FromSeconds(1)) return;

        PollRateHz = _pollsThisBucket / span.TotalSeconds;
        _pollsThisBucket = 0;
        _pollBucketStart = now;
    }

    // Caller holds the lock.
    private void Append(TraceKind kind, byte[]? payload, string? message)
    {
        if (_count == Capacity) Evicted++;
        _ring[_next] = new TraceEntry(++_sequence, _clock.Elapsed, kind, payload, message);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<TraceEntry> Snapshot(int take)
    {
        lock (_gate)
        {
            int n = Math.Min(take, _count);
            var result = new TraceEntry[n];
            for (int i = 0; i < n; i++)
            {
                // Walk back from the newest, wrapping around the start of the ring.
                int idx = (_next - 1 - i + Capacity) % Capacity;
                result[i] = _ring[idx];
            }
            return result;
        }
    }

    /// <summary>Empties the timeline and zeroes the counters, so the next thing to happen is the only thing on screen.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _next = 0;
            _count = 0;
            _sequence = 0;
            PacketsSent = 0;
            PacketsReceived = 0;
            BytesSent = 0;
            BytesReceived = 0;
            Timeouts = 0;
            Evicted = 0;
            PollRateHz = 0;
            _pollsThisBucket = 0;
            _pollBucketStart = _clock.Elapsed;
        }
    }
}
