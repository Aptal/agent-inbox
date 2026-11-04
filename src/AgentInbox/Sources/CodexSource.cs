using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentInbox.Sources;

/// <summary>
/// Reads Codex rollout JSONL files incrementally. Only turn-boundary event lines are
/// JSON-parsed; the rest of a large rollout is skipped with a substring check.
/// </summary>
public sealed partial class CodexSource : IAgentSource
{
    private static readonly Regex SessionIdPattern = new(
        "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppSettings _settings;
    private readonly Dictionary<string, CodexFileState> _states = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CodexIndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _indexStamp = DateTime.MinValue;

    public CodexSource(AppSettings settings) => _settings = settings;

    public string Id => "codex";
    public string Name => "Codex";
    public bool Enabled => _settings.EnableCodex;
    public string Root => _settings.EffectiveCodexRoot;

    public Task<SourceScanResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    private SourceScanResult Discover(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var sessions = new List<AgentSession>();
        string? error = null;
        var sessionsDirectory = Path.Combine(Root, "sessions");

        try
        {
            if (!Directory.Exists(sessionsDirectory))
            {
                return new SourceScanResult(Id, Root, sessions, "未找到 Codex 会话目录", stopwatch.ElapsedMilliseconds);
            }

            var index = LoadIndex();
            var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(_settings.LookbackDays, 1));
            var files = Directory.EnumerateFiles(sessionsDirectory, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= cutoff.UtcDateTime
                    || index.Values.Any(entry => entry.SessionId.Length > 0
                        && file.Name.Contains(entry.SessionId, StringComparison.OrdinalIgnoreCase)
                        && entry.UpdatedAt >= cutoff))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = ParseIncremental(file.FullName, cancellationToken);
                    var session = BuildSession(file, state, index);
                    if (session is not null) sessions.Add(session);
                }
                catch (Exception ex)
                {
                    Log.Warn($"codex parse failed for {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"codex scan failed: {ex.Message}");
        }

        stopwatch.Stop();
        return new SourceScanResult(Id, Root, sessions, error, stopwatch.ElapsedMilliseconds);
    }

    private AgentSession? BuildSession(FileInfo file, CodexFileState state, IReadOnlyDictionary<string, CodexIndexEntry> index)
    {
        var id = state.SessionId;
        if (string.IsNullOrWhiteSpace(id))
        {
            var match = SessionIdPattern.Match(file.Name);
            if (match.Success) id = match.Groups[1].Value;
        }
        if (string.IsNullOrWhiteSpace(id)) return null;

        var title = string.Empty;
        if (index.TryGetValue(id, out var entry) && !string.IsNullOrWhiteSpace(entry.ThreadName))
        {
            title = entry.ThreadName;
        }
        if (string.IsNullOrWhiteSpace(title)) title = EnsureTitleLoaded(file.FullName, state);
        if (string.IsNullOrWhiteSpace(title) && state.State == SessionState.Unknown) return null;
        if (string.IsNullOrWhiteSpace(title)) title = "Codex 会话 " + ShortId(id);
        if (state.State == SessionState.Unknown) return null;

        var lastActivity = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
        if (state.LastEventAt is { } eventTime && eventTime > lastActivity) lastActivity = eventTime;
        var completedAt = state.State is SessionState.Completed or SessionState.Interrupted
            ? state.MarkerTime ?? lastActivity
            : (DateTimeOffset?)null;
        var completionKey = state.State switch
        {
            SessionState.Running => $"run:{state.TurnId}",
            SessionState.Completed => $"turn:{state.TurnId}:{state.MarkerTicks}",
            SessionState.Interrupted => $"abort:{state.TurnId}:{state.MarkerTicks}",
            _ => $"unknown:{lastActivity.UtcTicks}",
        };

        return new AgentSession
        {
            Key = $"codex:{id}",
            SourceId = Id,
            SourceName = Name,
            SessionId = id,
            Title = SourceUtil.CleanTitle(title),
            Cwd = state.Cwd,
            CreatedAt = state.CreatedAt,
            LastActivityAt = lastActivity,
            CompletedAt = completedAt,
            State = state.State,
            CompletionKey = completionKey,
            Turn = state.TurnOrdinal,
        };
    }

    private CodexFileState ParseIncremental(string path, CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(path, out var state))
        {
            state = new CodexFileState();
            _states[path] = state;
        }

        var info = new FileInfo(path);
        var length = info.Length;
        if (length < state.Offset)
        {
            state.Reset();
        }

        if (length == state.Offset)
        {
            if (state.CreatedAt is null)
            {
                state.CreatedAt = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToLocalTime();
            }
            return state;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(state.Offset, SeekOrigin.Begin);
        var remaining = length - state.Offset;
        if (remaining > int.MaxValue - 1) remaining = int.MaxValue - 1;
        var buffer = new byte[remaining];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }

        var lastNewline = -1;
        for (var i = total - 1; i >= 0; i--)
        {
            if (buffer[i] == (byte)'\n')
            {
                lastNewline = i;
                break;
            }
        }

        if (lastNewline < 0) return state;

        var text = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        var wasAtStart = state.Offset == 0;
        state.Offset += lastNewline + 1;
        if (wasAtStart && text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
        if (state.CreatedAt is null)
        {
            state.CreatedAt = new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToLocalTime();
        }

        foreach (var line in SourceUtil.CompleteLines(text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessLine(line, state);
        }

        var lastEvent = SourceUtil.LastLineTimestamp(text);
        if (lastEvent is { } lastEventTime && (state.LastEventAt is null || lastEventTime > state.LastEventAt))
        {
            state.LastEventAt = lastEventTime;
        }

        return state;
    }

    private static void ProcessLine(string line, CodexFileState state)
    {
        var interesting = line.Contains("\"task_started\"", StringComparison.Ordinal)
            || line.Contains("\"task_complete\"", StringComparison.Ordinal)
            || line.Contains("\"turn_aborted\"", StringComparison.Ordinal)
            || line.Contains("\"session_meta\"", StringComparison.Ordinal)
            || line.Contains("\"user_message\"", StringComparison.Ordinal);
        if (!interesting) return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = root.TryGetProperty("timestamp", out var ts) ? SourceUtil.ParseIsoTimestamp(ts) : null;

            if (root.TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String
                && typeElement.GetString() == "session_meta")
            {
                if (root.TryGetProperty("payload", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    if (state.Cwd.Length == 0
                        && meta.TryGetProperty("cwd", out var cwd)
                        && cwd.ValueKind == JsonValueKind.String)
                    {
                        state.Cwd = cwd.GetString() ?? string.Empty;
                    }
                    if (state.SessionId.Length == 0)
                    {
                        if (meta.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            state.SessionId = id.GetString() ?? string.Empty;
                        else if (meta.TryGetProperty("session_id", out var sessionId) && sessionId.ValueKind == JsonValueKind.String)
                            state.SessionId = sessionId.GetString() ?? string.Empty;
                    }
                }
                return;
            }

            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String) return;
            var eventType = payloadType.GetString();

            switch (eventType)
            {
                case "task_started":
                    state.State = SessionState.Running;
                    state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                    state.TurnOrdinal = ReadInt(payload, "turn_ordinal") ?? state.TurnOrdinal;
                    state.MarkerTime = ReadUnixTimestamp(payload, "started_at")
                        ?? ReadUnixTimestamp(payload, "started_at_ms")
                        ?? timestamp ?? state.MarkerTime;
                    state.MarkerTicks = state.MarkerTime?.UtcTicks ?? 0;
                    break;

                case "task_complete":
                    state.State = SessionState.Completed;
                    state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                    state.TurnOrdinal = ReadInt(payload, "turn_ordinal") ?? state.TurnOrdinal;
                    state.MarkerTime = ReadUnixTimestamp(payload, "completed_at")
                        ?? ReadUnixTimestamp(payload, "completed_at_ms")
                        ?? timestamp ?? state.MarkerTime;
                    state.MarkerTicks = state.MarkerTime?.UtcTicks ?? 0;
                    break;

                case "turn_aborted":
                    state.State = SessionState.Interrupted;
                    state.TurnId = ReadString(payload, "turn_id") ?? state.TurnId;
                    state.MarkerTime = timestamp ?? state.MarkerTime;
                    state.MarkerTicks = state.MarkerTime?.UtcTicks ?? 0;
                    break;

                case "user_message":
                    if (state.TitleFallback.Length == 0)
                    {
                        var candidate = ReadString(payload, "message") ?? ReadString(payload, "text") ?? string.Empty;
                        if (IsUsableTitle(candidate)) state.TitleFallback = SourceUtil.CleanTitle(candidate);
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // A torn final line is normal while Codex is writing; the next scan retries.
        }
    }

    private static string EnsureTitleLoaded(string path, CodexFileState state)
    {
        if (state.TitleFallback.Length > 0) return state.TitleFallback;
        try
        {
            var head = SourceUtil.ReadHeadShared(path, 512 * 1024);
            foreach (var line in SourceUtil.CompleteLines(head))
            {
                if (!line.Contains("\"role\":\"user\"", StringComparison.Ordinal)
                    && !line.Contains("\"user_message\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var type)
                        && type.GetString() == "event_msg"
                        && root.TryGetProperty("payload", out var payload)
                        && payload.TryGetProperty("type", out var payloadType)
                        && payloadType.GetString() == "user_message")
                    {
                        state.TitleFallback = ReadString(payload, "message") ?? string.Empty;
                    }
                    else if (root.TryGetProperty("payload", out var item)
                        && item.TryGetProperty("role", out var role)
                        && role.GetString() == "user"
                        && item.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            {
                                var candidate = text.GetString() ?? string.Empty;
                                if (IsUsableTitle(candidate)) state.TitleFallback = SourceUtil.CleanTitle(candidate);
                                break;
                            }
                        }
                    }

                    if (state.TitleFallback.Length > 0)
                    {
                        state.TitleFallback = SourceUtil.CleanTitle(state.TitleFallback);
                        return state.TitleFallback;
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed line.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"codex title read failed for {Path.GetFileName(path)}: {ex.Message}");
        }
        return state.TitleFallback;
    }

    private Dictionary<string, CodexIndexEntry> LoadIndex()
    {
        var path = Path.Combine(Root, "session_index.jsonl");
        if (!File.Exists(path)) return _index;

        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _indexStamp && _index.Count > 0) return _index;

        var result = new Dictionary<string, CodexIndexEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String) continue;
                    var id = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var name = root.TryGetProperty("thread_name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                    var updated = root.TryGetProperty("updated_at", out var updatedElement)
                        ? SourceUtil.ParseIsoTimestamp(updatedElement) ?? DateTimeOffset.MinValue
                        : DateTimeOffset.MinValue;
                    result[id] = new CodexIndexEntry(id, name, updated);
                }
                catch (JsonException)
                {
                    // Ignore malformed index rows.
                }
            }
            _index = result;
            _indexStamp = stamp;
        }
        catch (Exception ex)
        {
            Log.Warn($"codex index read failed: {ex.Message}");
        }
        return _index;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) ? number : null;

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number) || number <= 0) return null;
        try
        {
            // Codex has shipped both seconds and milliseconds for turn boundaries.
            if (number >= 100_000_000_000_000) number /= 1000; // microseconds
            return number >= 100_000_000_000
                ? SourceUtil.FromUnixMilliseconds(number)
                : DateTimeOffset.FromUnixTimeSeconds(number).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

    private static bool IsUsableTitle(string value)
    {
        var text = value.TrimStart();
        if (text.Length < 3) return false;
        if (text.StartsWith('<')) return false;
        if (text.StartsWith("#", StringComparison.Ordinal)
            && (text.StartsWith("# Files mentioned", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("# AGENTS.md", StringComparison.OrdinalIgnoreCase))) return false;
        if (text.StartsWith("You are Codex", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.StartsWith("Caveat:", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private sealed record CodexIndexEntry(string SessionId, string ThreadName, DateTimeOffset UpdatedAt);

    private sealed class CodexFileState
    {
        public long Offset;
        public SessionState State = SessionState.Unknown;
        public string TurnId = string.Empty;
        public string SessionId = string.Empty;
        public string Cwd = string.Empty;
        public string TitleFallback = string.Empty;
        public DateTimeOffset? CreatedAt;
        public DateTimeOffset? LastEventAt;
        public DateTimeOffset? MarkerTime;
        public long MarkerTicks;
        public int? TurnOrdinal;

        public void Reset()
        {
            Offset = 0;
            State = SessionState.Unknown;
            TurnId = string.Empty;
            SessionId = string.Empty;
            Cwd = string.Empty;
            TitleFallback = string.Empty;
            CreatedAt = null;
            LastEventAt = null;
            MarkerTime = null;
            MarkerTicks = 0;
            TurnOrdinal = null;
        }
    }
}
