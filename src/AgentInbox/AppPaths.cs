namespace AgentInbox;

/// <summary>Filesystem locations owned by this app. Never writes into agent data directories.</summary>
public static class AppPaths
{
    public static string AppDataRoot { get; } = ResolveAppDataRoot();

    public static string StateFile => Path.Combine(AppDataRoot, "state.json");
    public static string SettingsFile => Path.Combine(AppDataRoot, "settings.json");
    public static string LogsDirectory => Path.Combine(AppDataRoot, "logs");
    public static string WebViewDataDirectory => Path.Combine(AppDataRoot, "WebView2");

    public static string UiDirectory => Path.Combine(AppContext.BaseDirectory, "ui");

    public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string ResolveAppDataRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("AGENTINBOX_HOME");
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--home", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return Path.GetFullPath(args[i + 1]);
            }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AgentInbox");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(WebViewDataDirectory);
    }
}
