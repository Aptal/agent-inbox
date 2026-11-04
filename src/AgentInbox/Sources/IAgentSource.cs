using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentInbox.Sources;

public sealed record SourceScanResult(
    string Id,
    string Root,
    IReadOnlyList<AgentSession> Sessions,
    string? Error,
    long ElapsedMs);

/// <summary>Read-only adapter for one agent product's local session store.</summary>
public interface IAgentSource
{
    string Id { get; }
    string Name { get; }
    bool Enabled { get; }
    string Root { get; }
    Task<SourceScanResult> DiscoverAsync(CancellationToken cancellationToken);
}

internal static class SourceUtil
{
    public static string CleanTitle(string? value, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ", StringComparison.Ordinal);
        text = text.Trim();
        if (text.Length <= maxLength) return text;
        return text[..Math.Max(1, maxLength - 1)].TrimEnd() + "…";
    }

    public static string FileStem(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? name[..^6]
            : Path.GetFileNameWithoutExtension(name);
    }

    public static DateTimeOffset FromUnixMilliseconds(long value)
        => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime();

    public static DateTimeOffset? ParseIsoTimestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToLocalTime()
            : null;
    }

    public static string ReadAllTextShared(string path, int retries = 3)
    {
        Exception? last = null;
        for (var i = 0; i < retries; i++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(15);
            }
        }
        throw last ?? new IOException($"cannot read {path}");
    }

    public static string ReadRangeShared(string path, long offset, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[length];
        var total = 0;
        while (total < length)
        {
            var read = stream.Read(buffer, total, length - total);
            if (read <= 0) break;
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    public static string ReadHeadShared(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Min(stream.Length, maxBytes);
        var buffer = new byte[length];
        var total = 0;
        while (total < length)
        {
            var read = stream.Read(buffer, total, length - total);
            if (read <= 0) break;
            total += read;
        }
        var text = Encoding.UTF8.GetString(buffer, 0, total);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static IEnumerable<string> CompleteLines(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var line = text[start..i].TrimEnd('\r');
            start = i + 1;
            if (line.Length > 0) yield return line;
        }
    }

    public static DateTimeOffset? LastLineTimestamp(string text)
    {
        var end = text.Length;
        while (end > 0 && (text[end - 1] == '\n' || text[end - 1] == '\r')) end--;
        if (end == 0) return null;
        var start = text.LastIndexOf('\n', end - 1);
        var line = text[(start + 1)..end];
        if (line.Length == 0 || !line.Contains("\"timestamp\"", StringComparison.Ordinal)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("timestamp", out var timestamp)
                ? ParseIsoTimestamp(timestamp)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
