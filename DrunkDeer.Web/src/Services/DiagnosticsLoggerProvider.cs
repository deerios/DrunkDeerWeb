using Microsoft.Extensions.Logging;

namespace DrunkDeer.Web.Services;

/// <summary>
/// Feeds the SDK's own log lines into the <see cref="DiagnosticsLog"/> timeline, alongside the
/// packets they describe.
/// </summary>
/// <remarks>
/// The session knows things the wire does not show. A dropped frame is the clearest case: on the
/// wire it looks like a request whose answer never arrived, which is indistinguishable from a
/// request whose answer is merely late — the session is the only party that knows which. Putting
/// its notes on the same timeline as the packets is the whole point, since what a reader needs is
/// "this write went out, then the loop lost the next frame", in that order.
/// </remarks>
public sealed class DiagnosticsLoggerProvider : ILoggerProvider
{
    private readonly DiagnosticsLog _log;

    public DiagnosticsLoggerProvider(DiagnosticsLog log) => _log = log;

    public ILogger CreateLogger(string categoryName) => new DiagnosticsLogger(_log, Shorten(categoryName));

    // "DrunkDeer.KeyboardSession" is a fixed prefix on every line worth reading here; the
    // timeline is narrow and the namespace carries nothing the reader doesn't already know.
    private static string Shorten(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    public void Dispose() { }

    private sealed class DiagnosticsLogger : ILogger
    {
        private readonly DiagnosticsLog _log;
        private readonly string _category;

        public DiagnosticsLogger(DiagnosticsLog log, string category)
        {
            _log = log;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The level filtering is configured centrally (see Program.cs) rather than decided here.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (exception is not null) message = $"{message} — {exception.GetType().Name}: {exception.Message}";

            _log.RecordNote($"{Abbreviate(logLevel)} {_category}: {message}");
        }

        private static string Abbreviate(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
