using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using AgentInbox.Sources;
using Application = System.Windows.Application;

namespace AgentInbox;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Thread? _showThread;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.EnsureCreated();
        Log.TrimOldLogs();
        Log.Info($"AgentInbox starting, version {typeof(App).Assembly.GetName().Version}");

        if (e.Args.Any(arg => string.Equals(arg, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            RunDiagnose(e.Args);
            Shutdown();
            return;
        }

        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        var settings = SettingsStore.Load();
        var silent = e.Args.Any(arg => string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(settings, silent);
        MainWindow = window;
        if (!silent) window.Show();

        _showThread = new Thread(() =>
        {
            while (true)
            {
                var waitHandle = _showEvent;
                if (waitHandle is null) break;
                try
                {
                    waitHandle.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainWindow is MainWindow window) window.ShowFromTray();
                }), DispatcherPriority.Normal);
            }
        })
        {
            IsBackground = true,
            Name = "AgentInbox single-instance listener",
        };
        _showThread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var waitHandle = _showEvent;
        _showEvent = null;
        if (waitHandle is not null)
        {
            try { waitHandle.Set(); } catch { /* ignore */ }
            try { waitHandle.Dispose(); } catch { /* ignore */ }
        }
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(true, "Local\\AgentInbox.SingleInstance", out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                EventWaitHandle.OpenExisting("Local\\AgentInbox.Show").Set();
            }
            catch (Exception ex)
            {
                Log.Warn($"single-instance signal failed: {ex.Message}");
            }
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentInbox.Show");
        return true;
    }

    private void RunDiagnose(string[] args)
    {
        try
        {
            var settings = SettingsStore.Load();
            var sources = new List<IAgentSource>();
            if (settings.EnableDsh) sources.Add(new DshSource(settings));
            if (settings.EnableCodex) sources.Add(new CodexSource(settings));
            if (settings.EnableClaude) sources.Add(new ClaudeSource(settings));

            var results = new List<object>();
            foreach (var source in sources)
            {
                var started = DateTimeOffset.Now;
                SourceScanResult result;
                try
                {
                    result = source.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    result = new SourceScanResult(source.Id, source.Root, Array.Empty<AgentSession>(), ex.Message,
                        (long)(DateTimeOffset.Now - started).TotalMilliseconds);
                }

                results.Add(new
                {
                    id = source.Id,
                    name = source.Name,
                    root = result.Root,
                    count = result.Sessions.Count,
                    error = result.Error,
                    elapsedMs = result.ElapsedMs,
                    sessions = result.Sessions.Select(session => new
                    {
                        key = session.Key,
                        title = session.Title,
                        cwd = session.Cwd,
                        state = session.State.ToString().ToLowerInvariant(),
                        turn = session.Turn,
                        completionKey = session.CompletionKey,
                        lastActivityAt = session.LastActivityAt,
                        completedAt = session.CompletedAt,
                    }),
                });
            }

            var payload = new
            {
                generatedAt = DateTimeOffset.Now,
                settings = new
                {
                    settings.LookbackDays,
                    dshRoot = settings.EffectiveDshRoot,
                    codexRoot = settings.EffectiveCodexRoot,
                    claudeRoot = settings.EffectiveClaudeRoot,
                },
                sources = results,
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
            var path = args.SkipWhile(arg => !string.Equals(arg, "--diagnose", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(path))
            {
                path = Path.Combine(Path.GetTempPath(), "AgentInbox-diagnose.json");
            }
            File.WriteAllText(path, json);
            Log.Info($"diagnose written to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("diagnose failed", ex);
        }
    }
}
