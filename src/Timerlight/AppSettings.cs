using System.Text.Json;
using System.Text.Json.Serialization;

namespace Timerlight;

/// <summary>
/// User preferences, persisted as JSON in %APPDATA%\Timerlight\settings.json.
/// Loading never throws: a missing or damaged file simply yields the defaults.
/// </summary>
internal sealed class AppSettings
{
    public const int MinTargetMinutes = 1;
    public const int MaxTargetMinutes = 24 * 60;

    /// <summary>How long a sitting may last before the icon starts blinking.</summary>
    public int TargetMinutes { get; set; } = 60;

    /// <summary>
    /// How long one pour of sand lasts before the glass is turned over, in minutes.
    /// 0 stretches a single pour across the whole interval and never turns the glass.
    /// </summary>
    public int FlipMinutes { get; set; } = 10;

    /// <summary>Blink the tray icon once the target has been reached.</summary>
    public bool BlinkWhenFinished { get; set; } = true;

    /// <summary>
    /// How long the icon signals the finished interval before the count restarts by itself,
    /// in minutes - the length of the break being asked for. 0 leaves it signalling until
    /// the icon is clicked.
    /// </summary>
    public int AutoResetMinutes { get; set; } = 5;

    /// <summary>Show a Windows notification once per cycle when the target is reached.</summary>
    public bool ShowNotification { get; set; } = true;

    /// <summary>Restart the count after this many minutes without keyboard/mouse input. 0 disables it.</summary>
    public int IdleResetMinutes { get; set; } = 10;

    /// <summary>Restart the count when the workstation is unlocked.</summary>
    public bool ResetOnUnlock { get; set; } = true;

    /// <summary>Restart the count when the machine resumes from sleep.</summary>
    public bool ResetOnResume { get; set; } = true;

    [JsonIgnore]
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Timerlight");

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults - a broken settings file must never block startup.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(DirectoryPath);

            // Write to a temporary file first so a crash mid-write cannot truncate the settings.
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to persist them is not worth interrupting the user.
        }
    }

    private void Normalize()
    {
        TargetMinutes = Math.Clamp(TargetMinutes, MinTargetMinutes, MaxTargetMinutes);
        FlipMinutes = Math.Clamp(FlipMinutes, 0, MaxTargetMinutes);
        AutoResetMinutes = Math.Clamp(AutoResetMinutes, 0, MaxTargetMinutes);
        IdleResetMinutes = Math.Clamp(IdleResetMinutes, 0, MaxTargetMinutes);
    }
}
