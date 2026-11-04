using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AgentInbox;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SessionScanner _scanner;
    private readonly WebBridge _bridge;
    private readonly TrayService _tray;
    private readonly DispatcherTimer _boundsTimer;
    private ScanSnapshot? _lastSnapshot;
    private bool _allowClose;
    private bool _webReady;
    private bool _dragging;
    private double _dragLeft;
    private double _dragTop;

    public MainWindow(AppSettings settings, bool silent)
    {
        InitializeComponent();
        _settings = settings;
        _scanner = new SessionScanner(settings);
        _bridge = new WebBridge(this, _scanner, _settings);
        _tray = new TrayService(this, _scanner, _settings);
        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _boundsTimer.Tick += (_, _) =>
        {
            _boundsTimer.Stop();
            RememberBounds();
        };

        ApplyBounds();
        Topmost = _settings.AlwaysOnTop;
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico", UriKind.Absolute));
        }
        catch
        {
            // The generated exe icon still applies when the resource cannot be loaded.
        }

        _scanner.Updated += OnScanUpdated;
        Loaded += OnLoaded;
        Closing += OnClosing;
        LocationChanged += (_, _) => ScheduleBounds();
        SizeChanged += (_, _) => ScheduleBounds();
        StateChanged += (_, _) => ScheduleBounds();

        _scanner.Start();
        Log.Info("main window created");
    }

    public SessionScanner Scanner => _scanner;
    public AppSettings Settings => _settings;

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = _settings.AlwaysOnTop;
    }

    public void HideToTray()
    {
        Hide();
    }

    public void ToggleTopmost()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        SettingsStore.Save(_settings);
        _tray.RefreshTopmost();
        _bridge.SendState(_lastSnapshot ?? _scanner.Snapshot());
    }

    public void ApplyTopmost(bool value)
    {
        _settings.AlwaysOnTop = value;
        Topmost = value;
        _tray.RefreshTopmost();
    }

    public void NotifyCompletion(AgentSession session)
    {
        if (!_settings.NotifyOnComplete) return;
        Log.Info($"completion notification: {session.Key} {session.Title}");
        _tray.ShowCompletion(session);
    }

    public FocusResult FocusAgent(AgentSession session)
    {
        // A topmost inbox would sit above the target window. Drop it for a moment so
        // the switch is visible, then restore the user's pin preference.
        var restoreTopmost = _settings.AlwaysOnTop && Topmost;
        if (restoreTopmost) Topmost = false;
        var result = WindowFocusService.Focus(session);
        if (!restoreTopmost) return result;
        if (!result.Ok)
        {
            Topmost = true;
            return result;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_settings.AlwaysOnTop && IsVisible) Topmost = true;
        };
        timer.Start();
        return result;
    }

    public void ExitApplication()
    {
        _allowClose = true;
        _boundsTimer.Stop();
        RememberBounds();
        SettingsStore.Save(_settings);
        try { _scanner.Dispose(); } catch { /* ignore */ }
        try { _tray.Dispose(); } catch { /* ignore */ }
        Close();
        Application.Current.Shutdown();
    }

    public void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogsDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _bridge.SendToast("无法打开日志目录：" + ex.Message, "error");
        }
    }

    public void ReloadSettings(AppSettings settings)
    {
        _settings.EnableDsh = settings.EnableDsh;
        _settings.EnableCodex = settings.EnableCodex;
        _settings.EnableClaude = settings.EnableClaude;
        _settings.DshRoot = settings.DshRoot;
        _settings.CodexRoot = settings.CodexRoot;
        _settings.ClaudeRoot = settings.ClaudeRoot;
        _settings.LookbackDays = settings.LookbackDays;
        _settings.ScanIntervalSeconds = settings.ScanIntervalSeconds;
        _settings.NotifyOnComplete = settings.NotifyOnComplete;
        _settings.AlwaysOnTop = settings.AlwaysOnTop;
        _settings.StartWithWindows = settings.StartWithWindows;
        _settings.Normalize();
        Topmost = _settings.AlwaysOnTop;
        SettingsStore.Save(_settings);
        AutostartService.SetEnabled(_settings.StartWithWindows);
        _scanner.UpdateSettings(_settings);
        _tray.RefreshTopmost();
        _bridge.SendState(_scanner.Snapshot());
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDirectory);
            await Web.EnsureCoreWebView2Async(environment);
            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            try
            {
                // This is a small always-on inbox; ask WebView2 to keep its footprint low.
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            }
            catch
            {
                // Older WebView2 runtimes do not expose the memory target level.
            }
            core.SetVirtualHostNameToFolderMapping("agent.inbox", AppPaths.UiDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += OnNavigationCompleted;
            _bridge.Attach(Web);
            _webReady = true;
            Web.Source = new Uri("https://agent.inbox/index.html");
            Log.Info($"web UI loading from {AppPaths.UiDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error("webview init failed", ex);
            LoadingOverlay.Visibility = Visibility.Visible;
            MessageBox.Show(this,
                "无法初始化 WebView2 运行时。\n\nWindows 10 可能需要安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message,
                "Agent 收件箱", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) LoadingOverlay.Visibility = Visibility.Collapsed;
        else Log.Warn($"web navigation failed: {e.WebErrorStatus}");
    }

    private void OnScanUpdated(ScanUpdate update)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _lastSnapshot = update.Snapshot;
            if (_webReady) _bridge.SendState(update.Snapshot);
            if (update.NewCompletions.Count > 0 && (!IsVisible || !IsActive))
            {
                foreach (var completion in update.NewCompletions.Take(2))
                {
                    NotifyCompletion(completion);
                }
            }
        }), DispatcherPriority.Background);
    }

    private void ScheduleBounds()
    {
        if (!IsLoaded) return;
        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    private void RememberBounds()
    {
        if (WindowState != WindowState.Normal) return;
        if (Left < SystemParameters.VirtualScreenLeft - 20
            || Top < SystemParameters.VirtualScreenTop - 20
            || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            return;
        }
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
    }

    private void ApplyBounds()
    {
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowLeft is { } left && _settings.WindowTop is { } top)
        {
            var leftVisible = left >= SystemParameters.VirtualScreenLeft - 40
                && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80;
            var topVisible = top >= SystemParameters.VirtualScreenTop - 40
                && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80;
            if (leftVisible && topVisible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                return;
            }
        }
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideToTray();
        _tray.ShowHiddenHint();
    }

    public void BeginWindowDrag()
    {
        _dragging = true;
        _dragLeft = Left;
        _dragTop = Top;
    }

    public void DragWindow(double deltaX, double deltaY)
    {
        if (!_dragging) return;
        Left = _dragLeft + deltaX;
        Top = _dragTop + deltaY;
    }

    public void EndWindowDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        RememberBounds();
    }
}
