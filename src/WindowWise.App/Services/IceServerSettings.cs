using SIPSorcery.Net;

namespace WindowWise.Services;

/// <summary>Validate manual settings and issued credentials consistently before converting them to WebRTC ICE configuration.</summary>
public static class IceServerSettings
{
    // Ignore hidden TURN inputs when enableTurn=false. This class does not implement STUN input integration.
    public static RTCConfiguration Create(bool enableTurn, string turnUrls,
        string username, string credential, bool relayOnly)
    {
        var servers = new List<RTCIceServer>();
        if (enableTurn)
        {
            string[] urls = Lines(turnUrls);
            if (urls.Length == 0 || urls.Any(url => !ValidUrl(url)))
                throw new ArgumentException("InvalidTurn");
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(credential))
                throw new ArgumentException("MissingTurnCredentials");
            // Keep the issued password unchanged; trimming it could change the credential.
            foreach (string url in urls)
                servers.Add(new RTCIceServer { urls = url, username = username.Trim(), credential = credential });
        }
        if (relayOnly && !enableTurn) throw new ArgumentException("RelayNeedsTurn");
        // The all policy allows direct and relay candidates; relay restricts connectivity to relayed paths.
        return new RTCConfiguration
        {
            iceServers = servers,
            iceTransportPolicy = relayOnly ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all
        };
    }

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.Ordinal).ToArray();

    // Use a temporary URI only to validate turn:host:port syntax; this does not make an HTTP request.
    // Reject embedded usernames/passwords in URLs and accept credentials only through their separate fields.
    private static bool ValidUrl(string url)
    {
        int colon = url.IndexOf(':');
        if (colon < 0 || url.Any(char.IsWhiteSpace)) return false;
        string scheme = url[..colon].ToLowerInvariant();
        if (scheme is not ("turn" or "turns")) return false;
        string address = url[(colon + 1)..];
        if (address.Contains('/') || address.Contains('\\')) return false;
        if (!Uri.TryCreate("http://" + address, UriKind.Absolute, out var parsed) ||
            string.IsNullOrWhiteSpace(parsed.Host) || parsed.UserInfo.Length > 0 || parsed.AbsolutePath != "/" ||
            parsed.Fragment.Length > 0 || parsed.Port == 0) return false;
        return parsed.Query.Length == 0 || parsed.Query is "?transport=udp" or "?transport=tcp";
    }
}
