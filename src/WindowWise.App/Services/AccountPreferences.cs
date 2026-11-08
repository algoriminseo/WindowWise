using System.IO;
using System.Text.Json;

namespace WindowWise.Services;

/// <summary>Store device preferences separately from authentication credentials.</summary>
public sealed class AccountPreferences
{
    private static readonly Lazy<AccountPreferences> Shared = new(() => new AccountPreferences());
    public static AccountPreferences Current => Shared.Value;
    private readonly string _path;
    public bool AudioDeviceAlertsEnabled { get; private set; } = true;
    public event EventHandler? Changed;

    public AccountPreferences(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "WindowWise", "account-preferences.json");
        try
        {
            if (File.Exists(_path))
                AudioDeviceAlertsEnabled = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_path))
                    ?.AudioDeviceAlertsEnabled ?? true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable preferences do not prevent the app from starting.
        }
    }

    public void SetAudioDeviceAlerts(bool enabled)
    {
        if (enabled == AudioDeviceAlertsEnabled) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Settings(enabled)));
        File.Move(temporaryPath, _path, overwrite: true);
        AudioDeviceAlertsEnabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Settings(bool AudioDeviceAlertsEnabled);
}
