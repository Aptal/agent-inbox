using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Clipboard = System.Windows.Clipboard;

namespace AgentInbox;

/// <summary>JSON message bridge between the WPF host and the local WebView UI.</summary>
public sealed class WebBridge
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly MainWindow _window;
    private readonly SessionScanner _scanner;
    private readonly AppSettings _settings;
    private WebView2? _web;

    public WebBridge(MainWindow window, SessionScanner scanner, AppSettings settings)
    {
        _window = window;
        _scanner = scanner;
        _settings = settings;
    }

    public void Attach(WebView2 web)
    {
        _web = web;
        if (web.CoreWebView2 is not null)
        {
            web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        }
    }

    public void SendState(ScanSnapshot? snapshot = null)
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(() => SendState(snapshot)));
            return;
        }

        var web = _web;
        if (web?.CoreWebView2 is null) return;
        var state = snapshot ?? _scanner.Snapshot();
        var payload = new
        {
            type = "state",
            rows = state.Rows.Select(row => new
            {
                key = row.Key,
                sourceId = row.SourceId,
                sourceName = row.SourceName,
                sessionId = row.SessionId,
                title = row.Title,
                cwd = row.Cwd,
                state = row.State,
                done = row.Done,
                seen = row.Seen,
                isNew = row.New,
                lastActivityAt = row.LastActivityAt,
                completedAt = row.CompletedAt,
                turn = row.Turn,
            }),
            counts = new
            {
                unread = state.Unread,
                running = state.Running,
                done = state.Done,
                total = state.Total,
            },
            sources = state.Sources.Select(source => new
            {
                id = source.Id,
                name = source.Name,
                enabled = source.Enabled,
                available = source.Available,
                root = source.Root,
                count = source.Count,
                lastScanMs = source.LastScanMs,
                error = source.Error,
            }),
            settings = PublicSettings(),
            defaults = new
            {
                dshRoot = AppSettings.DefaultDshRoot,
                codexRoot = AppSettings.DefaultCodexRoot,
                claudeRoot = AppSettings.DefaultClaudeRoot,
            },
            scanning = state.Scanning,
            lastScanAt = state.LastScanAt,
            version = typeof(WebBridge).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            appDataPath = AppPaths.AppDataRoot,
        };

        try
        {
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"state post failed: {ex.Message}");
        }
    }

    public void SendToast(string message, string kind = "info")
    {
        if (!_window.Dispatcher.CheckAccess())
        {
            _window.Dispatcher.BeginInvoke(new Action(() => SendToast(message, kind)));
            return;
        }
        var web = _web;
        if (web?.CoreWebView2 is null) return;
        try
        {
            web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "toast", message, kind }, Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"toast post failed: {ex.Message}");
        }
    }

    private object PublicSettings() => new
    {
        enableDsh = _settings.EnableDsh,
        enableCodex = _settings.EnableCodex,
        enableClaude = _settings.EnableClaude,
        dshRoot = _settings.DshRoot,
        codexRoot = _settings.CodexRoot,
        claudeRoot = _settings.ClaudeRoot,
        lookbackDays = _settings.LookbackDays,
        scanIntervalSeconds = _settings.ScanIntervalSeconds,
        notifyOnComplete = _settings.NotifyOnComplete,
        alwaysOnTop = _settings.AlwaysOnTop,
        startWithWindows = _settings.StartWithWindows,
    };

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return;
            var type = typeElement.GetString();

            switch (type)
            {
                case "ready":
                    SendState();
                    break;

                case "refresh":
                    _scanner.RequestScan(immediate: true);
                    SendToast("正在扫描本地会话…");
                    break;

                case "setDone":
                {
                    var key = GetString(root, "key");
                    if (key is null) return;
                    _scanner.SetDone(key, GetBool(root, "done"));
                    SendState();
                    break;
                }

                case "setSeen":
                {
                    var key = GetString(root, "key");
                    if (key is null) return;
                    _scanner.SetSeen(key);
                    SendState();
                    break;
                }

                case "focus":
                    FocusSession(GetString(root, "key"));
                    break;

                case "clearDone":
                    _scanner.ClearDone();
                    SendState();
                    SendToast("已清理完成的记录");
                    break;

                case "clearAll":
                    _scanner.ClearAll();
                    SendState();
                    SendToast("已清空收件箱");
                    break;

                case "resetData":
                    _scanner.ResetInbox();
                    _scanner.RequestScan(immediate: true);
                    SendState();
                    SendToast("收件箱数据已重置");
                    break;

                case "saveSettings":
                    SaveSettings(root);
                    break;

                case "copy":
                {
                    var text = GetString(root, "text");
                    if (string.IsNullOrEmpty(text)) return;
                    try
                    {
                        Clipboard.SetText(text);
                        SendToast("已复制到剪贴板");
                    }
                    catch (Exception ex)
                    {
                        SendToast("复制失败：" + ex.Message, "error");
                    }
                    break;
                }

                case "openLogs":
                    _window.OpenLogsFolder();
                    break;

                case "drag":
                case "dragStart":
                    _window.BeginWindowDrag();
                    break;

                case "dragMove":
                    _window.DragWindow(GetDouble(root, "dx"), GetDouble(root, "dy"));
                    break;

                case "dragEnd":
                    _window.EndWindowDrag();
                    break;

                case "minimize":
                    _window.WindowState = WindowState.Minimized;
                    break;

                case "hide":
                    _window.HideToTray();
                    break;

                case "close":
                    _window.Close();
                    break;

                case "toggleTopmost":
                    _window.ToggleTopmost();
                    break;
            }
        }
        catch (JsonException ex)
        {
            Log.Warn($"web message parse failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("web message failed", ex);
            SendToast("操作失败：" + ex.Message, "error");
        }
    }

    private void FocusSession(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var session = _scanner.FindSession(key);
        if (session is null)
        {
            SendToast("这条会话已经不在本地数据中。", "error");
            return;
        }

        _scanner.SetSeen(key);
        var result = _window.FocusAgent(session);
        Log.Info($"focus {session.Key}: ok={result.Ok} window={result.ProcessName} {result.WindowTitle}");
        SendToast(result.Message, result.Ok ? "info" : "error");
        SendState();
    }

    private void SaveSettings(JsonElement root)
    {
        var updated = _settings.Clone();
        if (root.TryGetProperty("enableDsh", out var enableDsh)) updated.EnableDsh = enableDsh.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("enableCodex", out var enableCodex)) updated.EnableCodex = enableCodex.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("enableClaude", out var enableClaude)) updated.EnableClaude = enableClaude.ValueKind == JsonValueKind.True;
        updated.DshRoot = GetString(root, "dshRoot") ?? string.Empty;
        updated.CodexRoot = GetString(root, "codexRoot") ?? string.Empty;
        updated.ClaudeRoot = GetString(root, "claudeRoot") ?? string.Empty;
        if (root.TryGetProperty("lookbackDays", out var lookback) && lookback.TryGetInt32(out var days)) updated.LookbackDays = days;
        if (root.TryGetProperty("scanIntervalSeconds", out var interval) && interval.TryGetInt32(out var seconds)) updated.ScanIntervalSeconds = seconds;
        if (root.TryGetProperty("notifyOnComplete", out var notify)) updated.NotifyOnComplete = notify.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("alwaysOnTop", out var topmost)) updated.AlwaysOnTop = topmost.ValueKind == JsonValueKind.True;
        if (root.TryGetProperty("startWithWindows", out var autostart)) updated.StartWithWindows = autostart.ValueKind == JsonValueKind.True;

        updated.Normalize();
        _window.ReloadSettings(updated);
        SendToast("设置已保存");
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number) ? number : 0;
}
