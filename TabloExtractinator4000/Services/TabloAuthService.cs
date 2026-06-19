using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Services;

// ---------------------------------------------------------------------------
// Manages cloud auth + local device session
//
// Cloud flow (lighthousetv.ewscloud.com):
//   POST /api/v2/login/               → Bearer access_token
//   GET  /api/v2/account/             → profiles[], devices[] (with local URL)
//   POST /api/v2/account/select/      → Lighthouse token
//
// Local device calls are HMAC-MD5 signed (keys baked into the Tablo iOS app,
// extracted by tablo2plex). See auth_flow.md for full derivation.
// ---------------------------------------------------------------------------
public class TabloAuthService
{
    private const string CloudHost       = "https://lighthousetv.ewscloud.com";
    private const string CloudUserAgent  = "Tablo-FAST/2.0.0 (Mobile; iPhone; iOS 16.6)";
    internal const string DeviceUserAgent = "Tablo-FAST/1.7.0 (Mobile; iPhone; iOS 18.4)";

    // These keys are embedded in the Tablo iOS app binary and were extracted
    // by the tablo2plex project (src/Encryption.js). They sign local API requests.
    internal const string HmacHashKey  = "6l8jU5N43cEilqItmT3U2M2PFM3qPziilXqau9ys";
    internal const string HmacDeviceKey = "ljpg6ZkwShVv8aI12E2LP55Ep8vq1uYDPvX0DdTB";

    private readonly HttpClient _http;

    // Session state — set after successful Login()
    public string?    DeviceUrl          { get; private set; }
    public string?    CloudAuthorization { get; private set; }
    public string?    LighthouseToken    { get; private set; }
    public DeviceInfo? Device            { get; private set; }
    public bool       IsAuthenticated    => DeviceUrl != null;

    public TabloAuthService(HttpClient http)
    {
        _http = http;
    }

    // Full auth flow. Throws on any failure with a descriptive message.
    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        // Step 1: Login
        var loginBody = JsonSerializer.Serialize(new { email, password });
        var loginJson = await CloudPostAsync("/api/v2/login/", loginBody, auth: null, ct);
        if (loginJson["code"] != null)
            throw new InvalidOperationException($"Login rejected: {loginJson["message"]}");

        var accessToken = loginJson["access_token"]!.GetValue<string>();
        CloudAuthorization = $"{loginJson["token_type"]!.GetValue<string>()} {accessToken}";

        // Step 2: Account — discover devices
        var acctJson = await CloudGetAsync("/api/v2/account/", ct);
        if (acctJson["code"] != null)
            throw new InvalidOperationException($"Account fetch rejected: {acctJson["message"]}");

        var profiles = acctJson["profiles"]!.AsArray();
        var devices  = acctJson["devices"]!.AsArray();
        if (profiles.Count == 0) throw new InvalidOperationException("No profiles on account.");
        if (devices.Count  == 0) throw new InvalidOperationException("No devices on account.");

        // For now: always use first profile and first device.
        // Multi-device support can be added later via a picker UI.
        var profile   = profiles[0]!;
        var device    = devices[0]!;
        var profileId = profile["identifier"]!.GetValue<string>();
        var serverId  = device["serverId"]!.GetValue<string>();
        DeviceUrl     = device["url"]!.GetValue<string>();

        Device = new DeviceInfo(
            ServerId:     serverId,
            Name:         device["name"]?.GetValue<string>() ?? "Tablo",
            Version:      device["version"]?.GetValue<string>() ?? "",
            ModelName:    "", // filled in from /server/info after connect
            Tuners:       0,
            LocalAddress: new Uri(DeviceUrl).Host
        );

        // Step 3: Select profile + device → Lighthouse token
        var selectBody = JsonSerializer.Serialize(new { pid = profileId, sid = serverId });
        var selectJson = await CloudPostAsync("/api/v2/account/select/", selectBody, CloudAuthorization, ct);
        LighthouseToken = selectJson["token"]?.GetValue<string>()
            ?? throw new InvalidOperationException("No Lighthouse token in select response.");
    }

    // Build the HMAC-MD5 Authorization header for a local device request.
    public static string MakeDeviceAuthHeader(string method, string path, string date, string bodyMd5 = "")
    {
        var signString = $"{method}\n{path}\n{bodyMd5}\n{date}";
        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(HmacHashKey));
        var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(signString))).ToLowerInvariant();
        return $"tablo:{HmacDeviceKey}:{hex}";
    }

    // ---------------------------------------------------------------------------
    // Private cloud HTTP helpers
    // ---------------------------------------------------------------------------
    private async Task<JsonNode> CloudGetAsync(string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, CloudHost + path);
        req.Headers.TryAddWithoutValidation("Authorization", CloudAuthorization);
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloud GET {path} → {resp.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON");
    }

    private async Task<JsonNode> CloudPostAsync(string path, string payload, string? auth, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, CloudHost + path);
        if (auth != null) req.Headers.TryAddWithoutValidation("Authorization", auth);
        req.Headers.TryAddWithoutValidation("User-Agent", CloudUserAgent);
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Cloud POST {path} → {resp.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON");
    }
}
