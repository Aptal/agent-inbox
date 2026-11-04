using System.Diagnostics;
using System.Text.Json;

namespace AgentInbox.Sources;

/// <summary>
/// Reads DSH's projection cache. DSH already folds titles and turn statistics into
/// small JSON files, so the inbox does not need to decompress active session logs.
/// </summary>
public sealed class DshSource : IAgentSource
{
    private readonly AppSettings _settings;
    private readonly Dictionary<string, DshCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, DateTimeOffset> _archivedCache = new(StringComparer.Ordinal);
    private DateTime _archivedStamp = DateTime.MinValue;

    public DshSource(AppSettings settings) => _settings = settings;

    public string Id => "dsh";
    public string Name => "DSH";
    public bool Enabled => _settings.EnableDsh;
    public string Root => _settings.EffectiveDshRoot;

    public Task<SourceScanResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Discover(cancellationToken), cancellationToken);
    }

    private SourceScanResult Discover(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var sessions = new List<AgentSession>();
        string? error = null;
        var cacheDirectory = Path.Combine(Root, "storages", "session_projcache", "sessions");

        try
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return new SourceScanResult(Id, Root, sessions, "未找到 DSH 会话缓存", stopwatch.ElapsedMilliseconds);
            }

            var archived = LoadArchived();
            foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    var stamp = info.LastWriteTimeUtc.Ticks;
                    var length = info.Length;
                    if (!_cache.TryGetValue(file, out var entry) || entry.Stamp != stamp || entry.Length != length)
                    {
                        var session = Parse(file, archived);
                        entry = new DshCacheEntry(stamp, length, session);
                        _cache[file] = entry;
                    }

                    if (entry.Session is not null
                        && !archived.Contains(entry.Session.SessionId)
                        && !archived.Contains("session-" + entry.Session.SessionId))
                    {
                        sessions.Add(entry.Session);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"dsh parse failed for {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Log.Warn($"dsh scan failed: {ex.Message}");
        }

        stopwatch.Stop();
        return new SourceScanResult(Id, Root, sessions, error, stopwatch.ElapsedMilliseconds);
    }

    private AgentSession? Parse(string file, IReadOnlySet<string> archived)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file);
        if (archived.Contains(sessionId) || archived.Contains("session-" + sessionId)) return null;

        var text = SourceUtil.ReadAllTextShared(file);
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        if (!root.TryGetProperty("record", out var record)) return null;
        if (!record.TryGetProperty("rows", out var rows)) return null;

        var metadata = GetVal(rows, "sessionListMetadata");
        if (metadata is { } meta && meta.TryGetProperty("blank", out var blank)
            && blank.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var title = ReadTitle(rows);
        var stats = GetVal(rows, "sessionStats");
        int? lastTurn = null;
        var running = false;
        if (stats is { } statsValue)
        {
            if (statsValue.TryGetProperty("lastTurn", out var turnElement)
                && turnElement.ValueKind == JsonValueKind.Number
                && turnElement.TryGetInt32(out var turn))
            {
                lastTurn = turn;
            }

            if (statsValue.TryGetProperty("openStep", out var openStep)
                && openStep.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                running = true;
            }

            if (statsValue.TryGetProperty("pendingCalls", out var pending)
                && pending.ValueKind == JsonValueKind.Object
                && pending.EnumerateObject().Any())
            {
                running = true;
            }
        }

        if (lastTurn is null && string.IsNullOrWhiteSpace(title)) return null;
        if (string.IsNullOrWhiteSpace(title)) title = "DSH 会话 " + ShortId(sessionId);

        var identity = record.TryGetProperty("identity", out var identityElement) ? identityElement : default;
        var cwd = identity.ValueKind == JsonValueKind.Object
            && identity.TryGetProperty("cwd", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String
                ? cwdElement.GetString() ?? string.Empty
                : string.Empty;

        DateTimeOffset? createdAt = null;
        if (identity.ValueKind == JsonValueKind.Object
            && identity.TryGetProperty("createdAt", out var createdElement)
            && createdElement.ValueKind == JsonValueKind.Number
            && createdElement.TryGetInt64(out var createdMs))
        {
            createdAt = SourceUtil.FromUnixMilliseconds(createdMs);
        }

        var lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero).ToLocalTime();
        DateTimeOffset? lastPrompt = null;
        if (metadata is { } metadataValue
            && metadataValue.TryGetProperty("lastPromptAt", out var promptElement)
            && promptElement.ValueKind == JsonValueKind.Number
            && promptElement.TryGetInt64(out var promptMs))
        {
            lastPrompt = SourceUtil.FromUnixMilliseconds(promptMs);
        }

        var lastActivity = lastPrompt is { } promptTime && promptTime > lastWrite ? promptTime : lastWrite;
        var state = running ? SessionState.Running
            : lastTurn is not null ? SessionState.Completed
            : SessionState.Unknown;

        if (state == SessionState.Unknown) return null;

        var completionKey = $"turn:{lastTurn}:{(running ? "run" : "done")}";
        return new AgentSession
        {
            Key = $"dsh:{sessionId}",
            SourceId = Id,
            SourceName = Name,
            SessionId = sessionId,
            Title = SourceUtil.CleanTitle(title),
            Cwd = cwd,
            CreatedAt = createdAt,
            LastActivityAt = lastActivity,
            CompletedAt = state == SessionState.Completed ? lastActivity : null,
            State = state,
            CompletionKey = completionKey,
            Turn = lastTurn,
        };
    }

    private static string ReadTitle(JsonElement rows)
    {
        var title = GetVal(rows, "title");
        if (title is { } titleValue && titleValue.ValueKind == JsonValueKind.String)
        {
            var text = titleValue.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        var titleInput = GetVal(rows, "titleInput");
        if (titleInput is { } input && input.TryGetProperty("first", out var first)
            && first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("text", out var textElement)
            && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static JsonElement? GetVal(JsonElement rows, string name)
    {
        if (!rows.TryGetProperty(name, out var unit) || unit.ValueKind != JsonValueKind.Object) return null;
        if (!unit.TryGetProperty("val", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value;
    }

    private HashSet<string> LoadArchived()
    {
        var path = Path.Combine(Root, "storages", "workspace.json");
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);

        var stamp = File.GetLastWriteTimeUtc(path);
        if (stamp == _archivedStamp && _archivedCache.Count > 0)
        {
            return new HashSet<string>(_archivedCache.Keys, StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(SourceUtil.ReadAllTextShared(path));
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("global", out var global)
                && global.TryGetProperty("archivedSessionIds", out var archived)
                && archived.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in archived.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) result[id] = default;
                    }
                }
            }

            _archivedCache = result;
            _archivedStamp = stamp;
            return new HashSet<string>(result.Keys, StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Warn($"dsh workspace parse failed: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static string ShortId(string id)
    {
        var trimmed = id.StartsWith("session-", StringComparison.Ordinal) ? id[8..] : id;
        return trimmed.Length <= 8 ? trimmed : trimmed[..8];
    }

    private sealed record DshCacheEntry(long Stamp, long Length, AgentSession? Session);
}
