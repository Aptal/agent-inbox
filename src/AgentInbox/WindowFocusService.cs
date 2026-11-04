using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentInbox;

public sealed record FocusResult(bool Ok, string Message, string WindowTitle = "", string ProcessName = "");

/// <summary>
/// Best-effort foreground activation of the application that owns an agent session.
/// No agent-specific automation is attempted; this only switches windows.
/// </summary>
public static class WindowFocusService
{
    private const int SwRestore = 9;
    private const uint WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    public static FocusResult Focus(AgentSession session)
    {
        var windows = EnumerateWindows();
        var best = windows
            .Select(window => (Window: window, Score: Score(window, session)))
            .Where(pair => pair.Score > 0)
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Window.Title.Length)
            .FirstOrDefault();

        if (best.Window.Handle == IntPtr.Zero)
        {
            return new FocusResult(false, "没有找到对应的应用窗口，请先打开该 Agent。");
        }

        var activated = Activate(best.Window.Handle);
        var note = NoteFor(session.SourceId, best.Window);
        var message = activated
            ? $"已切换到 {best.Window.ProcessName}：{Truncate(best.Window.Title, 60)}{note}"
            : $"找到了窗口，但系统拒绝切换前台。{note}";
        return new FocusResult(activated, message, best.Window.Title, best.Window.ProcessName);
    }

    public static void BeginDrag(IntPtr windowHandle)
    {
        ReleaseCapture();
        SendMessage(windowHandle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    private static string NoteFor(string sourceId, WindowInfo window)
    {
        if (sourceId == "dsh" && IsBrowser(window.ProcessName))
            return "（DSH 在浏览器标签页中，可在侧边栏按标题定位）";
        if (sourceId == "claude" && window.ProcessName is "WindowsTerminal" or "cmd" or "powershell" or "conhost")
            return "（终端可能包含多个标签页，请确认）";
        return string.Empty;
    }

    private static int Score(WindowInfo window, AgentSession session)
    {
        var title = window.Title;
        var process = window.ProcessName;
        var score = 0;

        switch (session.SourceId)
        {
            case "dsh":
                if (ContainsTitle(title, session.Title)) score += 130;
                if (title.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("DSH", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("Harness", StringComparison.OrdinalIgnoreCase)) score += 90;
                if (IsBrowser(process)) score += 12;
                break;

            case "codex":
                if (process.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)) score += 130;
                if (title.Contains("Codex", StringComparison.OrdinalIgnoreCase)) score += 100;
                if (process.Equals("codex", StringComparison.OrdinalIgnoreCase)) score += 50;
                if (title.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase)) score += 40;
                break;

            case "claude":
                if (title.Contains("Claude", StringComparison.OrdinalIgnoreCase)) score += 110;
                if (ContainsProject(title, session.Cwd)) score += 70;
                if (process.Equals("Code", StringComparison.OrdinalIgnoreCase)) score += 45;
                if (process.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)) score += 35;
                if (process is "cmd" or "powershell" or "conhost") score += 20;
                break;
        }

        return score;
    }

    private static bool ContainsTitle(string windowTitle, string sessionTitle)
    {
        if (string.IsNullOrWhiteSpace(sessionTitle)) return false;
        var value = sessionTitle.TrimEnd('…');
        if (value.Length < 4) return false;
        var probe = value.Length > 14 ? value[..14] : value;
        return windowTitle.Contains(probe, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsProject(string windowTitle, string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        var leaf = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return leaf.Length >= 3 && windowTitle.Contains(leaf, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBrowser(string process)
        => process is "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "Arc";

    private static bool Activate(IntPtr handle)
    {
        try
        {
            if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(handle, out _);
            var attached = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread
                && AttachThreadInput(foregroundThread, targetThread, true);
            try
            {
                var ok = SetForegroundWindow(handle);
                BringWindowToTop(handle);
                return ok || GetForegroundWindow() == handle;
            }
            finally
            {
                if (attached) AttachThreadInput(foregroundThread, targetThread, false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"focus failed: {ex.Message}");
            return false;
        }
    }

    private static List<WindowInfo> EnumerateWindows()
    {
        var result = new List<WindowInfo>();
        var ownPid = Environment.ProcessId;
        try
        {
            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle)) return true;
                var length = GetWindowTextLength(handle);
                if (length <= 0) return true;
                var builder = new StringBuilder(length + 1);
                if (GetWindowText(handle, builder, builder.Capacity) <= 0) return true;
                GetWindowThreadProcessId(handle, out var processId);
                if (processId == ownPid) return true;
                var processName = GetProcessName(processId);
                if (string.IsNullOrWhiteSpace(processName)) return true;
                result.Add(new WindowInfo(handle, (int)processId, processName, builder.ToString()));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"window enumeration failed: {ex.Message}");
        }
        return result;
    }

    private static string GetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    private readonly record struct WindowInfo(IntPtr Handle, int ProcessId, string ProcessName, string Title);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
