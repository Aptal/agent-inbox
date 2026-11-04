using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AgentInbox;

/// <summary>System tray presence: the window close button hides here instead of exiting.</summary>
public sealed class TrayService : IDisposable
{
    private readonly MainWindow _window;
    private readonly SessionScanner _scanner;
    private readonly AppSettings _settings;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _topmostItem;
    private bool _hiddenHintShown;
    private bool _disposed;

    public TrayService(MainWindow window, SessionScanner scanner, AppSettings settings)
    {
        _window = window;
        _scanner = scanner;
        _settings = settings;

        var menu = new Forms.ContextMenuStrip
        {
            Font = new Drawing.Font("Microsoft YaHei UI", 9f),
            ShowImageMargin = false,
        };
        var showItem = new Forms.ToolStripMenuItem("显示收件箱");
        showItem.Font = new Drawing.Font("Microsoft YaHei UI", 9f, Drawing.FontStyle.Bold);
        showItem.Click += (_, _) => ShowWindow();
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _topmostItem = new Forms.ToolStripMenuItem("始终置顶") { CheckOnClick = true };
        _topmostItem.Click += (_, _) =>
        {
            _window.Dispatcher.Invoke(() => _window.ApplyTopmost(_topmostItem.Checked));
            SettingsStore.Save(_settings);
        };
        menu.Items.Add(_topmostItem);

        var refresh = new Forms.ToolStripMenuItem("立即刷新");
        refresh.Click += (_, _) => _scanner.RequestScan(immediate: true);
        menu.Items.Add(refresh);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exit = new Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => _window.Dispatcher.Invoke(_window.ExitApplication);
        menu.Items.Add(exit);

        _icon = new Forms.NotifyIcon
        {
            Text = "Agent 收件箱",
            Visible = true,
            ContextMenuStrip = menu,
        };
        try
        {
            var path = Environment.ProcessPath;
            var extracted = path is null ? null : Drawing.Icon.ExtractAssociatedIcon(path);
            _icon.Icon = extracted ?? Drawing.SystemIcons.Application;
        }
        catch
        {
            _icon.Icon = Drawing.SystemIcons.Application;
        }

        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
        RefreshTopmost();
    }

    public void ShowCompletion(AgentSession session)
    {
        if (_disposed) return;
        try
        {
            var title = session.Title.Length > 60 ? session.Title[..60] + "…" : session.Title;
            _icon.ShowBalloonTip(4000, "对话完成", $"{session.SourceName} · {title}", Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Warn($"balloon failed: {ex.Message}");
        }
    }

    public void ShowHiddenHint()
    {
        if (_hiddenHintShown || _disposed) return;
        _hiddenHintShown = true;
        try
        {
            _icon.ShowBalloonTip(2500, "Agent 收件箱仍在运行",
                "窗口已隐藏到托盘；双击托盘图标可重新打开。", Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Notifications are optional.
        }
    }

    public void RefreshTopmost()
    {
        if (_disposed) return;
        _topmostItem.Checked = _settings.AlwaysOnTop;
    }

    private void ShowWindow()
    {
        if (_disposed) return;
        _window.Dispatcher.BeginInvoke(new Action(_window.ShowFromTray));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // Ignore tray teardown failures.
        }
    }
}
