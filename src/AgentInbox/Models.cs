using System.Text.Json.Serialization;

namespace AgentInbox;

/// <summary>Lifecycle of one agent conversation as observed from local session data.</summary>
public enum SessionState
{
    Unknown = 0,
    Running = 1,
    Completed = 2,
    Interrupted = 3,
}

/// <summary>One discovered conversation, normalized across agent products.</summary>
public sealed class AgentSession
{
    public required string Key { get; init; }
    public required string SourceId { get; init; }
    public required string SourceName { get; init; }
    public required string SessionId { get; init; }
    public required string Title { get; init; }
    public string Cwd { get; init; } = string.Empty;
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? LastActivityAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public SessionState State { get; init; }
    public string CompletionKey { get; init; } = string.Empty;
    public int? Turn { get; init; }
}

/// <summary>Persisted inbox membership and user-owned read/done/cleared state.</summary>
public sealed class InboxItem
{
    public string Key { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Cwd { get; set; } = string.Empty;
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string CompletionKey { get; set; } = string.Empty;
    public bool Seen { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public bool Hidden { get; set; }
    public DateTimeOffset? HiddenAt { get; set; }
}

/// <summary>State written to disk.</summary>
public sealed class InboxStateFile
{
    public int Version { get; set; } = 1;
    public Dictionary<string, InboxItem> Items { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Row view model sent to the WebView.</summary>
public sealed record InboxRow(
    string Key,
    string SourceId,
    string SourceName,
    string SessionId,
    string Title,
    string Cwd,
    string State,
    bool Done,
    bool Seen,
    bool New,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? CompletedAt,
    int? Turn);

public sealed record SourceStatus(
    string Id,
    string Name,
    bool Enabled,
    bool Available,
    string Root,
    int Count,
    long LastScanMs,
    string? Error);

public sealed record ScanSnapshot(
    IReadOnlyList<InboxRow> Rows,
    IReadOnlyList<SourceStatus> Sources,
    int Unread,
    int Running,
    int Done,
    int Total,
    DateTimeOffset? LastScanAt,
    bool Scanning);

public sealed record NewCompletion(AgentSession Session, int UnreadCount);

public sealed class MergeResult
{
    public required IReadOnlyList<InboxRow> Rows { get; init; }
    public required IReadOnlyList<AgentSession> NewCompletions { get; init; }
    public required int Unread { get; init; }
    public required int Running { get; init; }
    public required int DoneCount { get; init; }
}
