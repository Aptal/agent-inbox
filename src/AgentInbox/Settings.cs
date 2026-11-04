using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentInbox;

public sealed class AppSettings
{
    public bool EnableDsh { get; set; } = true;
    public bool EnableCodex { get; set; } = true;
    public bool EnableClaude { get; set; } = true;

    /// <summary>Blank means the default location under the user profile.</summary>
    public string DshRoot { get; set; } = string.Empty;
    public string CodexRoot { get; set; } = string.Empty;
    public string ClaudeRoot { get; set; } = string.Empty;

    public int LookbackDays { get; set; } = 3;
    public int ScanIntervalSeconds { get; set; } = 4;
    public bool NotifyOnComplete { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool StartWithWindows { get; set; }

    public double WindowWidth { get; set; } = 420;
    public double WindowHeight { get; set; } = 640;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    public static string DefaultDshRoot => Path.Combine(AppPaths.HomeDirectory, ".dsh");
    public static string DefaultCodexRoot => Path.Combine(AppPaths.HomeDirectory, ".codex");
    public static string DefaultClaudeRoot => Path.Combine(AppPaths.HomeDirectory, ".claude");

    [JsonIgnore]
    public string EffectiveDshRoot => string.IsNullOrWhiteSpace(DshRoot) ? DefaultDshRoot : DshRoot.Trim();
    [JsonIgnore]
    public string EffectiveCodexRoot => string.IsNullOrWhiteSpace(CodexRoot) ? DefaultCodexRoot : CodexRoot.Trim();
    [JsonIgnore]
    public string EffectiveClaudeRoot => string.IsNullOrWhiteSpace(ClaudeRoot) ? DefaultClaudeRoot : ClaudeRoot.Trim();

    public void Normalize()
    {
        LookbackDays = Math.Clamp(LookbackDays, 1, 90);
        ScanIntervalSeconds = Math.Clamp(ScanIntervalSeconds, 2, 120);
        WindowWidth = Math.Clamp(WindowWidth, 360, 900);
        WindowHeight = Math.Clamp(WindowHeight, 480, 1200);
        DshRoot = DshRoot?.Trim() ?? string.Empty;
        CodexRoot = CodexRoot?.Trim() ?? string.Empty;
        ClaudeRoot = ClaudeRoot?.Trim() ?? string.Empty;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"settings load failed: {ex.Message}");
        }

        var fallback = new AppSettings();
        fallback.Normalize();
        return fallback;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(settings, Options);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings save failed: {ex.Message}");
        }
    }
}
