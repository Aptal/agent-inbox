using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentInbox;

/// <summary>
/// Owns the user's inbox state. The agent data is read-only; this store only keeps
/// membership, read, done, and clear tombstones in %APPDATA%.
/// </summary>
public sealed class InboxStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, InboxItem> _items = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    public InboxStore()
    {
        Load();
    }

    public MergeResult Merge(IReadOnlyList<AgentSession> sessions, int lookbackDays)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            var cutoff = now.AddDays(-Math.Max(1, lookbackDays));
            var changed = false;
            var newCompletions = new List<AgentSession>();

            foreach (var session in sessions)
            {
                if (_items.TryGetValue(session.Key, out var item))
                {
                    var completionChanged = !string.Equals(item.CompletionKey, session.CompletionKey, StringComparison.Ordinal);
                    var completedAfterStart = session.State == SessionState.Completed
                        && (session.CompletedAt ?? now) >= _startedAt.AddSeconds(-2);

                    item.SourceId = session.SourceId;
                    item.SessionId = session.SessionId;
                    if (!string.Equals(item.Title, session.Title, StringComparison.Ordinal)
                        || !string.Equals(item.Cwd, session.Cwd, StringComparison.Ordinal))
                    {
                        changed = true;
                    }
                    item.Title = session.Title;
                    item.Cwd = session.Cwd;
                    item.LastActivityAt = session.LastActivityAt;
                    item.CompletionKey = session.CompletionKey;
                    item.CompletedAt = session.CompletedAt;
                    if (completionChanged) changed = true;

                    if (session.State == SessionState.Running && item.Done && completionChanged)
                    {
                        // A new turn started after the user marked the conversation done.
                        item.Done = false;
                        item.DoneAt = null;
                        item.Seen = true;
                        changed = true;
                    }

                    if (session.State == SessionState.Running && item.Hidden && completionChanged)
                    {
                        item.Hidden = false;
                        item.HiddenAt = null;
                        item.Seen = true;
                        changed = true;
                    }

                    if (completionChanged && session.State == SessionState.Completed)
                    {
                        item.Done = false;
                        item.DoneAt = null;
                        item.Hidden = false;
                        item.HiddenAt = null;
                        item.Seen = !completedAfterStart;
                        changed = true;
                        if (completedAfterStart) newCompletions.Add(session);
                    }
                    else if (completionChanged && session.State == SessionState.Interrupted)
                    {
                        item.Done = false;
                        item.DoneAt = null;
                        item.Seen = !completedAfterStart;
                        changed = true;
                    }

                }
                else
                {
                    var lastActivity = session.LastActivityAt ?? session.CompletedAt ?? session.CreatedAt;
                    var eligible = session.State == SessionState.Running
                        || (lastActivity is { } activity && activity >= cutoff);
                    if (!eligible) continue;

                    var completedAfterStart = session.State == SessionState.Completed
                        && (session.CompletedAt ?? now) >= _startedAt.AddSeconds(-2);
                    var recentlyCompleted = session.State == SessionState.Completed
                        && (session.CompletedAt ?? DateTimeOffset.MinValue) >= now.AddHours(-2);
                    item = new InboxItem
                    {
                        Key = session.Key,
                        SourceId = session.SourceId,
                        SessionId = session.SessionId,
                        Title = session.Title,
                        Cwd = session.Cwd,
                        AddedAt = now,
                        LastActivityAt = session.LastActivityAt,
                        CompletedAt = session.CompletedAt,
                        CompletionKey = session.CompletionKey,
                        Seen = !(completedAfterStart || recentlyCompleted),
                    };
                    _items[session.Key] = item;
                    changed = true;
                    if (completedAfterStart) newCompletions.Add(session);
                }
            }

            var rows = BuildRows(sessions);
            if (changed) Save();
            return new MergeResult
            {
                Rows = rows,
                NewCompletions = newCompletions,
                Unread = rows.Count(row => row.New),
                Running = rows.Count(row => row.State == "running"),
                DoneCount = rows.Count(row => row.Done),
            };
        }
    }

    public void SetDone(string key, bool done)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(key, out var item)) return;
            item.Done = done;
            item.DoneAt = done ? DateTimeOffset.Now : null;
            if (done) item.Seen = true;
            Save();
        }
    }

    public void SetSeen(string key)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var item) && !item.Seen)
            {
                item.Seen = true;
                Save();
            }
        }
    }

    public void ClearDone()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in _items.Values.Where(item => item.Done))
            {
                item.Hidden = true;
                item.HiddenAt = now;
            }
            Save();
        }
    }

    public void ClearAll()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            foreach (var item in _items.Values)
            {
                item.Hidden = true;
                item.HiddenAt = now;
            }
            Save();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
    }

    public InboxItem? Get(string key)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out var item) ? item : null;
        }
    }

    public IReadOnlyList<InboxRow> Rows(IReadOnlyList<AgentSession> sessions)
    {
        lock (_gate) return BuildRows(sessions);
    }

    private List<InboxRow> BuildRows(IReadOnlyList<AgentSession> sessions)
    {
        var rows = new List<InboxRow>(sessions.Count);
        foreach (var session in sessions)
        {
            if (!_items.TryGetValue(session.Key, out var item) || item.Hidden) continue;
            var isNew = !item.Seen && !item.Done && session.State == SessionState.Completed;
            rows.Add(new InboxRow(
                session.Key,
                session.SourceId,
                session.SourceName,
                session.SessionId,
                session.Title,
                session.Cwd,
                StateName(session.State),
                item.Done,
                item.Seen,
                isNew,
                session.LastActivityAt,
                session.CompletedAt,
                session.Turn));
        }

        rows.Sort((left, right) =>
        {
            var rank = Rank(left).CompareTo(Rank(right));
            if (rank != 0) return rank;
            var leftTime = left.CompletedAt ?? left.LastActivityAt ?? DateTimeOffset.MinValue;
            var rightTime = right.CompletedAt ?? right.LastActivityAt ?? DateTimeOffset.MinValue;
            return rightTime.CompareTo(leftTime);
        });
        return rows;
    }

    private static int Rank(InboxRow row)
    {
        if (row.New) return 0;
        if (row.State == "running") return 1;
        if (row.Done) return 3;
        return 2;
    }

    private static string StateName(SessionState state) => state switch
    {
        SessionState.Running => "running",
        SessionState.Completed => "completed",
        SessionState.Interrupted => "interrupted",
        _ => "unknown",
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.StateFile)) return;
            var json = File.ReadAllText(AppPaths.StateFile);
            var state = JsonSerializer.Deserialize<InboxStateFile>(json, Options);
            if (state?.Items is null) return;
            foreach (var pair in state.Items)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                pair.Value.Key = pair.Key;
                _items[pair.Key] = pair.Value;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"inbox state load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            var state = new InboxStateFile
            {
                Version = 1,
                Items = _items,
            };
            AtomicFile.WriteAllText(AppPaths.StateFile, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"inbox state save failed: {ex.Message}");
        }
    }
}
