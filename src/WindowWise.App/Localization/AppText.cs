using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WindowWise.Localization;

/// <summary>One display language for the application; protocol values and user data stay unchanged.</summary>
public sealed class AppText : INotifyPropertyChanged
{
    private static readonly Dictionary<string, string> English = Load("en");
    private static readonly Dictionary<string, string> Korean = Load("ko");
    private static readonly Dictionary<string, string> SourceKeys = English
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
    public static AppText Current { get; } = new();
    private readonly List<Func<bool>> _refresh = [];
    private string _language = "en";
    private string? _preferencePath;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Language
    {
        get => _language;
        set
        {
            if (value is not ("en" or "ko")) throw new ArgumentOutOfRangeException(nameof(value));
            if (_language == value) return;
            // Persist before publishing, so a failed save cannot leave controls disagreeing.
            if (_preferencePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_preferencePath))!);
                string temporaryPath = _preferencePath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Preference(value)));
                File.Move(temporaryPath, _preferencePath, overwrite: true);
            }
            ApplyLanguage(value);
        }
    }
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "ko" ? "ko-KR" : "en-US");
    public int SelectedIndex
    {
        get => Language == "ko" ? 0 : 1;
        set { if (value is 0 or 1) Language = value == 0 ? "ko" : "en"; }
    }

    /// <summary>Called at startup. Tests can supply an isolated path without changing user preferences.</summary>
    public void UsePreferences(string path)
    {
        _preferencePath = path;
        string language = "en";
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Preference>(File.ReadAllText(path))?.Language is "ko")
                language = "ko";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged preference file must not prevent the application from opening.
        }
        ApplyLanguage(language);
    }

    public string this[string key] => (Language == "ko" ? Korean : English)[key];
    public static string Source(string key) => English[key];
    public string Text(string source, params object[] arguments)
    {
        string format = SourceKeys.TryGetValue(source, out string? key) ? this[key] : source;
        return arguments.Length == 0 ? format : string.Format(Culture, format, arguments);
    }

    /// <summary>Refresh computed display properties without keeping closed views or removed items alive.</summary>
    public void Track<T>(T target, Action<T> refresh) where T : class
    {
        var reference = new WeakReference<T>(target);
        lock (_refresh)
            _refresh.Add(() =>
            {
                if (!reference.TryGetTarget(out var live)) return false;
                refresh(live);
                return true;
            });
    }

    private void ApplyLanguage(string language)
    {
        _language = language;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedIndex)));
        Func<bool>[] callbacks;
        lock (_refresh) callbacks = [.. _refresh];
        foreach (var refresh in callbacks)
            if (!refresh()) { lock (_refresh) _refresh.Remove(refresh); }
    }

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(AppText).Assembly.GetManifestResourceStream($"WindowWise.Localization.App.{language}.json")
            ?? throw new InvalidOperationException($"Missing application catalog: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Invalid application catalog: {language}");
    }
    private sealed record Preference(string Language);
}

/// <summary>Retain a message template and its data so a visible status can be translated again.</summary>
public sealed record LocalizedMessage(string Source, params object[] Arguments)
{
    public override string ToString() => AppText.Current.Text(Source, Arguments);
    public static implicit operator LocalizedMessage(string source) => new(source);
}
