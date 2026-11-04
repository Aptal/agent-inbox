using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentInbox.Sources;

/// <summary>
/// Reads Claude Code transcripts under ~/.claude/projects. Turn completion is inferred
/// from the last assistant stop reason plus Claude Code's own end-of-turn markers.
/// </summary>
public sealed class ClaudeSource : IAgentSource
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, ClaudeFileState> _states = new(StringComparer.OrdinalIgnoreCase);

    public ClaudeSource(AppSettings settings) => _settings = settings;

    public string Id => "claude";
    public string Name => "Claude Code";
    public bool Enabled => _settings.EnableClaude;
    public string Root => _settings.EffectiveClaudeRoot;

    public Task<SourceScanResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    private SourceScanResult Discover(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var sessions = new List<AgentSession>();
        string? error = null;
        var projectsDirectory = Path.Combine(Root, "projects");

        try
        {
            if (!Directory.Exists(projectsDirectory))
            {
                return new SourceScanResult(Id, Root, sessions, "未找到 Claude Code 会话目录", stopwatch.ElapsedMilliseconds);
            }

            var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(_settings.LookbackDays, 1));
            var files = Directory.EnumerateFiles(projectsDirectory, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(file => file.LastWriteTimeUtc >= cutoff.UtcDateTime)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var state = ParseIncremental(file.FullName, cancellationToken);
                    var session = BuildSession(file, state);
                    if (session is not null) sessions.Add(session);
                }
                catch (Exception ex)
                {
                    Log.Warn($"claude parse failed for {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"claude scan failed: {ex.Message}");
        }

        stopwatch.Stop();
        return new SourceScanResult(Id, Root, sessions, error, stopwatch.ElapsedMilliseconds);
    }

    private AgentSession? BuildSession(FileInfo file, ClaudeFileState state)
    {
        var id = string.IsNullOrWhiteSpace(state.SessionId) ? file.Name[..^6] : state.SessionId;
        if (string.IsNullOrWhiteSpace(id)) return null;

        var title = EnsureTitleLoaded(file.FullName, state);
        if (string.IsNullOrWhiteSpace(title) && state.State == SessionState.Unknown) return null;
        if (string.IsNullOrWhiteSpace(title)) title = "Claude 会话 " + ShortId(id);
        if (state.State == SessionState.Unknown) return null;

        var lastActivity = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
        if (state.LastEventAt is { } eventTime && eventTime > lastActivity) lastActivity = eventTime;
        var completedAt = state.State == SessionState.Completed
            ? state.MarkerTime ?? lastActivity
            : (DateTimeOffset?)null;
        var completionKey = state.State switch
        {
            SessionState.Running => $"run:{state.TurnId}",
            SessionState.Completed => $"turn:{state.TurnId}:{(state.TurnId.Length > 0 ? 0 : state.MarkerTicks)}",
            _ => $"unknown:{lastActivity.UtcTicks}",
        };

        return new AgentSession
        {
            Key = $"claude:{id}",
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
        };
    }

    private ClaudeFileState ParseIncremental(string path, CancellationToken cancellationToken)
    {
        if (!_states.TryGetValue(path, out var state))
        {
            state = new ClaudeFileState();
            _states[path] = state;
        }

        var info = new FileInfo(path);
        var length = info.Length;
        if (length < state.Offset) state.Reset();
        if (length == state.Offset)
        {
            state.CreatedAt ??= new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToLocalTime();
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
        state.CreatedAt ??= new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero).ToLocalTime();

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

    private static void ProcessLine(string line, ClaudeFileState state)
    {
        var interesting = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"user\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"last-prompt\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"cost-state\"", StringComparison.Ordinal)
            || line.Contains("\"stop_hook_summary\"", StringComparison.Ordinal)
            || line.Contains("\"type\":\"summary\"", StringComparison.Ordinal)
            || line.Contains("\"cwd\":", StringComparison.Ordinal);
        if (!interesting) return;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) return;
            if (root.TryGetProperty("isMeta", out var isMeta) && isMeta.ValueKind == JsonValueKind.True) return;

            if (state.Cwd.Length == 0
                && root.TryGetProperty("cwd", out var cwd)
                && cwd.ValueKind == JsonValueKind.String)
            {
                state.Cwd = cwd.GetString() ?? string.Empty;
            }

            if (state.SessionId.Length == 0
                && root.TryGetProperty("sessionId", out var sessionId)
                && sessionId.ValueKind == JsonValueKind.String)
            {
                state.SessionId = sessionId.GetString() ?? string.Empty;
            }

            var timestamp = root.TryGetProperty("timestamp", out var timestampElement)
                ? SourceUtil.ParseIsoTimestamp(timestampElement)
                : null;
            var uuid = root.TryGetProperty("uuid", out var uuidElement) && uuidElement.ValueKind == JsonValueKind.String
                ? uuidElement.GetString() ?? string.Empty
                : string.Empty;

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return;
            var type = typeElement.GetString();

            switch (type)
            {
                case "assistant":
                    if (root.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.Object
                        && message.TryGetProperty("stop_reason", out var stopReason)
                        && stopReason.ValueKind == JsonValueKind.String)
                    {
                        var reason = stopReason.GetString();
                        if (reason == "tool_use")
                        {
                            SetRunning(state, uuid, timestamp);
                        }
                        else if (reason is "end_turn" or "stop_sequence" or "max_tokens")
                        {
                            SetCompleted(state, uuid, timestamp);
                        }
                    }
                    break;

                case "user":
                    SetRunning(state, uuid, timestamp);
                    break;

                case "last-prompt":
                    if (root.TryGetProperty("lastPrompt", out var lastPrompt) && lastPrompt.ValueKind == JsonValueKind.String)
                    {
                        var prompt = lastPrompt.GetString();
                        if (!string.IsNullOrWhiteSpace(prompt))
                        {
                            state.LastPrompt = SourceUtil.CleanTitle(prompt);
                        }
                    }
                    SetCompleted(state, uuid, timestamp);
                    break;

                case "cost-state":
                    SetCompleted(state, uuid, timestamp);
                    break;

                case "system":
                    if (root.TryGetProperty("subtype", out var subtype)
                        && subtype.ValueKind == JsonValueKind.String
                        && subtype.GetString() == "stop_hook_summary")
                    {
                        SetCompleted(state, uuid, timestamp);
                    }
                    break;

                case "summary":
                    if (state.Summary.Length == 0
                        && root.TryGetProperty("summary", out var summary)
                        && summary.ValueKind == JsonValueKind.String)
                    {
                        state.Summary = SourceUtil.CleanTitle(summary.GetString());
                    }
                    break;
            }
        }
        catch (JsonException)
        {
            // Torn final line; retried next scan.
        }
    }

    private static void SetRunning(ClaudeFileState state, string uuid, DateTimeOffset? timestamp)
    {
        state.State = SessionState.Running;
        if (uuid.Length > 0) state.TurnId = uuid;
        else if (timestamp is { } time) state.TurnId = "t" + time.ToUnixTimeMilliseconds();
        state.MarkerTime = timestamp ?? state.MarkerTime;
        state.MarkerTicks = state.MarkerTime?.UtcTicks ?? 0;
    }

    private static void SetCompleted(ClaudeFileState state, string uuid, DateTimeOffset? timestamp)
    {
        if (state.State == SessionState.Completed && state.TurnId.Length > 0 && uuid.Length == 0)
        {
            // last-prompt / cost-state trails the assistant message for the same turn.
            state.MarkerTime = timestamp ?? state.MarkerTime;
            return;
        }

        state.State = SessionState.Completed;
        if (uuid.Length > 0) state.TurnId = uuid;
        else if (timestamp is { } time) state.TurnId = "t" + time.ToUnixTimeMilliseconds();
        state.MarkerTime = timestamp ?? state.MarkerTime;
        state.MarkerTicks = state.MarkerTime?.UtcTicks ?? 0;
    }

    private static string EnsureTitleLoaded(string path, ClaudeFileState state)
    {
        if (state.TitleLoaded) return FirstNonEmpty(state.TitleFallback, state.Summary, state.LastPrompt);
        state.TitleLoaded = true;
        try
        {
            var head = SourceUtil.ReadHeadShared(path, 512 * 1024);
            foreach (var line in SourceUtil.CompleteLines(head))
            {
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("isSidechain", out var sidechain) && sidechain.ValueKind == JsonValueKind.True) continue;
                    if (root.TryGetProperty("isMeta", out var isMeta) && isMeta.ValueKind == JsonValueKind.True) continue;
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (!message.TryGetProperty("role", out var role) || role.GetString() != "user") continue;
                    if (!message.TryGetProperty("content", out var content)) continue;

                    var text = ExtractText(content);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.OrdinalIgnoreCase)) continue;
                    state.TitleFallback = SourceUtil.CleanTitle(text);
                    return state.TitleFallback;
                }
                catch (JsonException)
                {
                    // Skip malformed line.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"claude title read failed for {Path.GetFileName(path)}: {ex.Message}");
        }

        return FirstNonEmpty(state.TitleFallback, state.Summary, state.LastPrompt);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() is not "text" and not "input_text") continue;
            if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];

    private sealed class ClaudeFileState
    {
        public long Offset;
        public SessionState State = SessionState.Unknown;
        public string TurnId = string.Empty;
        public string SessionId = string.Empty;
        public string Cwd = string.Empty;
        public string TitleFallback = string.Empty;
        public string Summary = string.Empty;
        public string LastPrompt = string.Empty;
        public bool TitleLoaded;
        public DateTimeOffset? CreatedAt;
        public DateTimeOffset? LastEventAt;
        public DateTimeOffset? MarkerTime;
        public long MarkerTicks;

        public void Reset()
        {
            Offset = 0;
            State = SessionState.Unknown;
            TurnId = string.Empty;
            SessionId = string.Empty;
            Cwd = string.Empty;
            TitleFallback = string.Empty;
            Summary = string.Empty;
            LastPrompt = string.Empty;
            TitleLoaded = false;
            CreatedAt = null;
            LastEventAt = null;
            MarkerTime = null;
            MarkerTicks = 0;
        }
    }
}
