using System.IO;
using System.Text.Json;

namespace OnTimeShutdown.Services;

public sealed class ScheduledShutdownState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public DateTimeOffset DueUtc { get; set; }
    public int TotalSeconds { get; set; }
    public string Mode { get; set; } = "time";
    public string TargetLabel { get; set; } = string.Empty;
    public long BootMarkerTicks { get; set; }

    public TimeSpan Remaining(DateTimeOffset now)
    {
        TimeSpan remaining = DueUtc - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}

public sealed class UserPreferences
{
    public string LastMode { get; set; } = "time";
    public int Hour { get; set; } = -1;
    public int Minute { get; set; } = -1;
    public bool Tomorrow { get; set; }
    public int DurationMinutes { get; set; } = 30;
}

/// <summary>
/// A tiny portable JSON store living next to the executable.
/// Keeping it portable makes the app easy to move or delete.
/// </summary>
public sealed class StateStore
{
    private readonly string _dataDirectory;
    private readonly string _stateFile;
    private readonly string _preferencesFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public StateStore()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        _stateFile = Path.Combine(_dataDirectory, "plan.json");
        _preferencesFile = Path.Combine(_dataDirectory, "settings.json");
    }

    public ScheduledShutdownState? LoadState()
    {
        try
        {
            if (!File.Exists(_stateFile))
            {
                return null;
            }

            string json = File.ReadAllText(_stateFile);
            var state = JsonSerializer.Deserialize<ScheduledShutdownState>(json, JsonOptions);
            return state is { Version: 1, DueUtc: { } due } && due > DateTimeOffset.MinValue
                ? state
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveState(ScheduledShutdownState state)
    {
        WriteAtomically(_stateFile, JsonSerializer.Serialize(state, JsonOptions));
    }

    public void ClearState()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                File.Delete(_stateFile);
            }
        }
        catch
        {
            // A stale plan is harmless: it only affects this UI.
        }
    }

    public UserPreferences LoadPreferences()
    {
        try
        {
            if (File.Exists(_preferencesFile))
            {
                string json = File.ReadAllText(_preferencesFile);
                return JsonSerializer.Deserialize<UserPreferences>(json, JsonOptions) ?? new UserPreferences();
            }
        }
        catch
        {
            // Fall through to defaults.
        }

        return new UserPreferences();
    }

    public void SavePreferences(UserPreferences preferences)
    {
        try
        {
            WriteAtomically(_preferencesFile, JsonSerializer.Serialize(preferences, JsonOptions));
        }
        catch
        {
            // Preferences are a convenience; never interrupt the user for this.
        }
    }

    private void WriteAtomically(string path, string contents)
    {
        Directory.CreateDirectory(_dataDirectory);
        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }
}
