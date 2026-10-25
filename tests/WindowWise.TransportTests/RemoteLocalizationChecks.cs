using System.Text.Json;
using WindowWise.Localization;
using WindowWise.Services;

internal static class RemoteLocalizationChecks
{
    // Verify catalog coverage and formatting before opening any network connections.
    public static void Run()
    {
        var korean = Catalog("ko");
        var english = Catalog("en");
        Check(korean.Keys.Order().SequenceEqual(english.Keys.Order()), "Korean and English catalog keys match");
        var text = new RemoteSharingText();
        foreach (var key in korean.Keys)
        {
            string ko = text.Format(key, "A", "B", "C", "D");
            text.Language = "en";
            string en = text.Format(key, "A", "B", "C", "D");
            if (string.IsNullOrWhiteSpace(ko) || string.IsNullOrWhiteSpace(en) ||
                en.Any(character => character is >= '\uAC00' and <= '\uD7A3'))
                throw new InvalidOperationException($"Incomplete translation: {key}");
            text.Language = "ko";
        }
        Check(true, "All catalog messages format in Korean and English");
        int changes = 0;
        text.PropertyChanged += (_, args) => { if (args.PropertyName == "Item[]") changes++; };
        text.Language = "en";
        Check(changes == 1 && text["Send"] == "Send file", "Language changes notify live bindings");
        var otherPeer = new RemoteSharingText();
        Check(otherPeer["Send"] == "파일 보내기", "Each peer chooses its own display language");
        foreach (var state in Enum.GetValues<RemoteTransferState>())
            Check(text[$"State.{state}"] != otherPeer[$"State.{state}"], $"Progress translation: {state}");
        var error = new RemoteFileTransferException("receiver-canceled");
        Check(text[$"Error.{error.Code}"] != otherPeer[$"Error.{error.Code}"], "Peer error codes are translated locally");
    }

    // Read the same embedded catalogs used by the remote sharing view.
    private static Dictionary<string, string> Catalog(string language)
    {
        using var stream = typeof(RemoteSharingText).Assembly.GetManifestResourceStream($"WindowWise.Localization.RemoteSharing.{language}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }

    // Fail the integration runner if a localized message or notification is missing.
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {description}");
        Console.WriteLine($"PASS: {description}");
    }
}
