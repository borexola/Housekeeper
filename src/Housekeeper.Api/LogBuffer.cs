namespace Housekeeper.Api;

/// <summary>One line of the app's own log, kept in memory so the Logs page can show and search it.</summary>
public sealed record LogEntry(
    DateTimeOffset AtUtc,
    LogLevel Level,
    /// <summary>Which part of the app spoke: Scan, Home Assistant, Model, Drafting, Concerns, Live feed, Settings, Service.</summary>
    string Flow,
    /// <summary>The logger category's last segment, e.g. AnomalyScanner.</summary>
    string Source,
    string Message,
    string? Exception);

/// <summary>
/// The most recent few thousand log lines, in memory.
///
/// The container's log is the record; this is the window onto it that the Logs page needs, kept here
/// rather than read back from the console because a log line's category and level are structure that a
/// text stream has lost. A ring, so memory is bounded whatever the scan interval; lines fall off the back.
/// </summary>
public sealed class LogBuffer(int capacity = 5000)
{
    private readonly LogEntry?[] _ring = new LogEntry?[Math.Max(16, capacity)];
    private readonly Lock _lock = new();
    private int _next;
    private long _total;

    public int Capacity => _ring.Length;

    /// <summary>Every line ever added, including those that have since fallen off the back.</summary>
    public long Total
    {
        get { lock (_lock) return _total; }
    }

    public void Add(LogEntry entry)
    {
        lock (_lock)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % _ring.Length;
            _total++;
        }
    }

    /// <summary>The held lines, newest first.</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_lock)
        {
            var count = (int)Math.Min(_total, _ring.Length);
            var lines = new LogEntry[count];
            for (var i = 0; i < count; i++)
                lines[i] = _ring[((_next - 1 - i) % _ring.Length + _ring.Length) % _ring.Length]!;
            return lines;
        }
    }

    /// <summary>
    /// Which part of the app a logger category belongs to, in the words the page uses. A reader looking for
    /// "what happened when it talked to Home Assistant" should not need to know class names.
    /// </summary>
    public static string FlowOf(string category)
    {
        var name = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        if (name.Contains("HomeAssistantClient", StringComparison.Ordinal) || name.Contains("EventStream", StringComparison.Ordinal) || name.Contains("EntityRegistry", StringComparison.Ordinal))
            return "Home Assistant";
        if (name.Contains("LlmClient", StringComparison.Ordinal)) return "Model";
        if (name.Contains("AnomalyScanner", StringComparison.Ordinal) || name.Contains("ScanWorker", StringComparison.Ordinal)) return "Scan";
        if (name.Contains("StateFeedWorker", StringComparison.Ordinal)) return "Live feed";
        if (name.Contains("ProposalService", StringComparison.Ordinal)) return "Drafting";
        if (name.Contains("ConcernService", StringComparison.Ordinal)) return "Concerns";
        if (name.Contains("Settings", StringComparison.Ordinal) || name.Contains("SecretStore", StringComparison.Ordinal)) return "Settings";
        if (category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal)) return "Host";
        return "Service";
    }

    public static string SourceOf(string category) =>
        category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

    /// <summary>The known flows, in the order the page offers them.</summary>
    public static readonly string[] Flows =
        ["Scan", "Home Assistant", "Model", "Live feed", "Drafting", "Concerns", "Settings", "Service", "Host"];
}

/// <summary>Feeds every log line into the buffer alongside whatever else logging is configured to do.</summary>
public sealed class LogBufferProvider(LogBuffer buffer, TimeProvider clock) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new BufferLogger(buffer, clock, categoryName);

    public void Dispose() { }

    private sealed class BufferLogger(LogBuffer buffer, TimeProvider clock, string category) : ILogger
    {
        private readonly string _flow = LogBuffer.FlowOf(category);
        private readonly string _source = LogBuffer.SourceOf(category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.None) return;

            var message = formatter(state, exception);
            buffer.Add(new LogEntry(clock.GetUtcNow(), logLevel, _flow, _source, message, exception?.ToString()));
        }
    }
}
