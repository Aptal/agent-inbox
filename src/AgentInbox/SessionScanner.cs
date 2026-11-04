using AgentInbox.Sources;
using Timer = System.Threading.Timer;

namespace AgentInbox;

public sealed record ScanUpdate(ScanSnapshot Snapshot, IReadOnlyList<AgentSession> NewCompletions);

/// <summary>
/// Orchestrates adapters, file watchers, and the inbox store. Scans never block the UI
/// thread and a slow source cannot prevent the other sources from publishing results.
/// </summary>
public sealed class SessionScanner : IDisposable
{
    private readonly object _gate = new();
    private readonly InboxStore _store = new();
    private readonly Dictionary<string, SourceStatus> _statuses = new(StringComparer.Ordinal);
    private List<AgentSession> _sessions = new();
    private List<IAgentSource> _sources = new();
    private List<FileSystemWatcher> _watchers = new();
    private Timer? _timer;
    private Timer? _debounce;
    private AppSettings _settings;
    private int _scanning;
    private int _pendingScan;
    private DateTimeOffset? _lastScanAt;
    private bool _disposed;

    public SessionScanner(AppSettings settings)
    {
        _settings = settings.Clone();
        _debounce = new Timer(_ => _ = ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
        RebuildSources();
    }

    public event Action<ScanUpdate>? Updated;

    public void Start()
    {
        RestartWatchers();
        _timer = new Timer(_ => RequestScan(), null, TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(Math.Max(2, _settings.ScanIntervalSeconds)));
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings.Clone();
        RebuildSources();
        RestartWatchers();
        _timer?.Change(TimeSpan.FromMilliseconds(50),
            TimeSpan.FromSeconds(Math.Max(2, _settings.ScanIntervalSeconds)));
        RequestScan(immediate: true);
    }

    public void RequestScan(bool immediate = false)
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pendingScan, 1);
        if (Volatile.Read(ref _scanning) == 1) return;
        _debounce?.Change(immediate ? 10 : 350, Timeout.Infinite);
    }

    public ScanSnapshot Snapshot()
    {
        lock (_gate)
        {
            var rows = _store.Rows(_sessions);
            return new ScanSnapshot(
                rows,
                _statuses.Values.OrderBy(status => SourceRank(status.Id)).ToList(),
                rows.Count(row => row.New),
                rows.Count(row => row.State == "running"),
                rows.Count(row => row.Done),
                rows.Count,
                _lastScanAt,
                Volatile.Read(ref _scanning) == 1);
        }
    }

    public void RefreshRows()
    {
        Publish(Array.Empty<AgentSession>());
    }

    public void SetDone(string key, bool done)
    {
        _store.SetDone(key, done);
        Publish(Array.Empty<AgentSession>());
    }

    public void SetSeen(string key)
    {
        _store.SetSeen(key);
        Publish(Array.Empty<AgentSession>());
    }

    public void ClearDone()
    {
        _store.ClearDone();
        Publish(Array.Empty<AgentSession>());
    }

    public void ClearAll()
    {
        _store.ClearAll();
        Publish(Array.Empty<AgentSession>());
    }

    public void ResetInbox()
    {
        _store.Reset();
        Publish(Array.Empty<AgentSession>());
    }

    public AgentSession? FindSession(string key)
    {
        lock (_gate) return _sessions.FirstOrDefault(session => session.Key == key);
    }

    private void RebuildSources()
    {
        _sources = new List<IAgentSource>();
        var dsh = new DshSource(_settings);
        var codex = new CodexSource(_settings);
        var claude = new ClaudeSource(_settings);
        lock (_gate)
        {
            _statuses.Clear();
            AddStatus(dsh, _settings.EnableDsh);
            AddStatus(codex, _settings.EnableCodex);
            AddStatus(claude, _settings.EnableClaude);
        }

        if (_settings.EnableDsh) _sources.Add(dsh);
        if (_settings.EnableCodex) _sources.Add(codex);
        if (_settings.EnableClaude) _sources.Add(claude);
    }

    private void AddStatus(IAgentSource source, bool enabled)
    {
        _statuses[source.Id] = new SourceStatus(source.Id, source.Name, enabled,
            Directory.Exists(source.Root), source.Root, 0, 0, null);
    }

    private void RestartWatchers()
    {
        foreach (var watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { /* ignore */ }
        }
        _watchers.Clear();

        if (_settings.EnableDsh) AddWatcher(Path.Combine(_settings.EffectiveDshRoot, "storages", "session_projcache", "sessions"));
        if (_settings.EnableCodex) AddWatcher(Path.Combine(_settings.EffectiveCodexRoot, "sessions"));
        if (_settings.EnableClaude) AddWatcher(Path.Combine(_settings.EffectiveClaudeRoot, "projects"));    }

    private void AddWatcher(string directory)
    {
        if (!Directory.Exists(directory)) return;
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true,
            };
            watcher.Changed += OnWatchedChange;
            watcher.Created += OnWatchedChange;
            watcher.Deleted += OnWatchedChange;
            watcher.Renamed += OnWatchedChange;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Log.Warn($"watcher failed for {directory}: {ex.Message}");
        }
    }

    private void OnWatchedChange(object sender, FileSystemEventArgs e)
    {
        var name = e.Name ?? string.Empty;
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
        RequestScan();
    }

    private async Task ScanAsync()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Interlocked.Exchange(ref _pendingScan, 0);
        try
        {
            _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
            var collected = new List<AgentSession>();
            foreach (var source in _sources)
            {
                if (_disposed) return;
                SourceScanResult result;
                var started = DateTimeOffset.Now;
                try
                {
                    result = await source.DiscoverAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new SourceScanResult(source.Id, source.Root, Array.Empty<AgentSession>(), ex.Message,
                        (long)(DateTimeOffset.Now - started).TotalMilliseconds);
                }

                collected.AddRange(result.Sessions);
                lock (_gate)
                {
                    _statuses[source.Id] = new SourceStatus(source.Id, source.Name, source.Enabled,
                        Directory.Exists(source.Root), result.Root, result.Sessions.Count, result.ElapsedMs, result.Error);
                    _sessions = collected.OrderByDescending(session => session.LastActivityAt).ToList();
                }

                var merge = _store.Merge(collected, _settings.LookbackDays);
                _lastScanAt = DateTimeOffset.Now;
                Publish(merge.NewCompletions, merge.Rows);
            }

            if (_sources.Count == 0)
            {
                lock (_gate) _sessions = new List<AgentSession>();
            }
        }
        catch (Exception ex)
        {
            Log.Error("scan failed", ex);
        }
        finally
        {
            Volatile.Write(ref _scanning, 0);
            _lastScanAt = DateTimeOffset.Now;
            Publish(Array.Empty<AgentSession>());
            if (!_disposed && Volatile.Read(ref _pendingScan) == 1)
            {
                _debounce?.Change(50, Timeout.Infinite);
            }
        }
    }

    private void Publish(IReadOnlyList<AgentSession> newCompletions, IReadOnlyList<InboxRow>? rows = null)
    {
        ScanSnapshot snapshot;
        lock (_gate)
        {
            var actualRows = rows ?? _store.Rows(_sessions);
            snapshot = new ScanSnapshot(
                actualRows,
                _statuses.Values.OrderBy(status => SourceRank(status.Id)).ToList(),
                actualRows.Count(row => row.New),
                actualRows.Count(row => row.State == "running"),
                actualRows.Count(row => row.Done),
                actualRows.Count,
                _lastScanAt,
                Volatile.Read(ref _scanning) == 1);
        }
        try
        {
            Updated?.Invoke(new ScanUpdate(snapshot, newCompletions));
        }
        catch (Exception ex)
        {
            Log.Warn($"scan publish failed: {ex.Message}");
        }
    }

    private static int SourceRank(string id) => id switch
    {
        "dsh" => 0,
        "codex" => 1,
        "claude" => 2,
        _ => 3,
    };

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _debounce?.Dispose();
        foreach (var watcher in _watchers)
        {
            try { watcher.Dispose(); } catch { /* ignore */ }
        }
        _watchers.Clear();
    }
}
