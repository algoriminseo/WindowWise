using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace WindowWise.Localization;

public sealed class RemoteSharingText : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, string> Korean = Load("ko");
    private static readonly IReadOnlyDictionary<string, string> English = Load("en");
    private string _language = "ko";

    public event PropertyChangedEventHandler? PropertyChanged;
    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language == "en" ? "en-US" : "ko-KR");
    public string this[string key] => (_language == "en" ? English : Korean)[key];

    public string Language
    {
        get => _language;
        set
        {
            if (value is not ("ko" or "en")) throw new ArgumentOutOfRangeException(nameof(value));
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    // Format translated UI messages without changing file or connection state.
    public string Format(string key, params object[] arguments) => string.Format(Culture, this[key], arguments);

    // Load the embedded catalog so deployed builds do not need loose translation files.
    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(RemoteSharingText).Assembly.GetManifestResourceStream($"WindowWise.Localization.RemoteSharing.{language}.json")
            ?? throw new InvalidOperationException($"Missing remote sharing catalog: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Invalid remote sharing catalog: {language}");
    }
}
