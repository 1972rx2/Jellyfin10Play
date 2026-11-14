using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TenPlay.Configuration;

namespace Jellyfin.Plugin.TenPlay.Services;

/// <summary>Minimal client for the official 10 Play device-login endpoints.</summary>
public static class TenPlayApiClient
{
    private const string ConfigUrl = "https://10.com.au/api/v1/config";
    private const string ActivateUrl = "https://10.com.au/activate";
    private const string SignatureKeyHex = "b918ff793563080c5821c89ee6c415c363cb36d369db1020369ac4b405a0211d";
    private static readonly HttpClientHandler SharedHandler = CreateHandler();
    private static readonly HttpClient Client = CreateClient(SharedHandler);
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    /// <summary>The user agent used for provider stream sessions.</summary>
    public const string StreamUserAgent = "otg/1.5.1 (AppleTv Apple TV 4; tvOS16.0; appletv.client) libcurl/7.58.0 OpenSSL/1.0.2o zlib/1.2.11 clib/1.8.56";

    /// <summary>Gets the provider configuration containing the Shows menu.</summary>
    public static Task<JsonDocument> GetCatalogueConfigurationAsync(CancellationToken cancellationToken)
    {
        return GetConfigurationAsync(cancellationToken);
    }

    /// <summary>Gets a Shows category using an endpoint from the provider configuration.</summary>
    public static async Task<JsonDocument> GetShowsCategoryAsync(string label, CancellationToken cancellationToken)
    {
        using JsonDocument config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        foreach (JsonElement menu in config.RootElement.GetProperty("menuConfig").GetProperty("primary").EnumerateArray())
        {
            if (menu.GetProperty("type").GetString() != "Shows")
            {
                continue;
            }

            foreach (JsonElement category in menu.GetProperty("subItems").EnumerateArray())
            {
                if (string.Equals(category.GetProperty("label").GetString(), label, StringComparison.Ordinal))
                {
                    return await GetJsonAsync(category.GetProperty("apiEndpoint").GetString()!, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException("This 10 Play Shows category is no longer available. Reopen Shows to refresh the list.");
    }

    /// <summary>Gets a show's seasons.</summary>
    public static async Task<JsonDocument> GetShowAsync(string id, CancellationToken cancellationToken)
    {
        using JsonDocument config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        string endpoint = config.RootElement.GetProperty("showsApiEndpoint").GetString()!;
        return await GetJsonAsync(endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(id) + "?includeAllSubNavs=true", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the episode list for a season.</summary>
    public static async Task<JsonDocument> GetSeasonAsync(string showId, string seasonId, CancellationToken cancellationToken)
    {
        using JsonDocument config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        string endpoint = config.RootElement.GetProperty("videosApiEndpoint").GetString()!;
        return await GetJsonAsync(endpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(showId) + "/episodes/" + Uri.EscapeDataString(seasonId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the provider's normal on-demand HLS session at playback time.</summary>
    public static async Task<string> GetPlaybackAsync(string id, CancellationToken cancellationToken)
    {
        PluginConfiguration configuration = TenPlayPlugin.Instance?.Configuration ?? throw new InvalidOperationException("10 Play is not initialized.");
        using JsonDocument config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await TokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (configuration.TokenExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                string refreshEndpoint = config.RootElement.GetProperty("authConfig").GetProperty("refreshToken").GetString()!;
                using JsonDocument tokens = await PostJsonAsync(refreshEndpoint, new { alternativeToken = configuration.AccessToken, refreshToken = configuration.RefreshToken }, cancellationToken).ConfigureAwait(false);
                SaveTokens(configuration, tokens.RootElement);
                TenPlayPlugin.Instance!.SaveConfiguration(configuration);
            }
        }
        finally
        {
            TokenLock.Release();
        }

        string endpoint = config.RootElement.GetProperty("videosApiEndpoint").GetString()!;
        string url = endpoint.TrimEnd('/') + "/playback/" + Uri.EscapeDataString(id) + "?device=Tv&platform=lg&appVersion=v1";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("tp-acceptfeature", "v1/fw;v1/drm;v2/live");
        request.Headers.TryAddWithoutValidation("tp-platform", "UAP");
        request.Headers.TryAddWithoutValidation("X-N10-SIG", CreateSignature(url));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", configuration.AccessToken);
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument playback = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        JsonElement dai = playback.RootElement.GetProperty("dai");
        string streamEndpoint = "https://dai.google.com/ondemand/hls/content/" + Uri.EscapeDataString(dai.GetProperty("contentSourceId").GetString()!) + "/vid/" + Uri.EscapeDataString(dai.GetProperty("videoId").GetString()!) + "/streams";
        var form = new Dictionary<string, string>();
        if (response.Headers.TryGetValues("X-DAI-AUTH", out IEnumerable<string>? auth))
        {
            form["auth-token"] = string.Join(string.Empty, auth);
        }

        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, streamEndpoint) { Content = new FormUrlEncodedContent(form) };
        streamRequest.Headers.TryAddWithoutValidation("User-Agent", StreamUserAgent);
        using HttpResponseMessage streamResponse = await Client.SendAsync(streamRequest, cancellationToken).ConfigureAwait(false);
        streamResponse.EnsureSuccessStatusCode();
        using JsonDocument stream = await JsonDocument.ParseAsync(await streamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        string manifest = stream.RootElement.GetProperty("stream_manifest").GetString()!;
        if (!Uri.TryCreate(manifest, UriKind.Absolute, out Uri? manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("10 Play did not return an HTTPS stream manifest.");
        }

        return manifest;
    }

    /// <summary>Starts a device-code login. The user completes it at 10 Play's activation site.</summary>
    public static async Task<DeviceLoginStart> StartDeviceLoginAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using JsonDocument config = await GetJsonAsync(ConfigUrl + "?SystemName=android&manufacturer=nvidia", cancellationToken).ConfigureAwait(false);
        string endpoint = config.RootElement.GetProperty("authConfig").GetProperty("generateCode").GetString()
            ?? throw new InvalidOperationException("10 Play did not supply a device-code endpoint.");

        using JsonDocument response = await PostJsonAsync(
            endpoint,
            new Dictionary<string, object>
            {
                ["deviceIdentifier"] = configuration.DeviceIdentifier,
                ["machine"] = "Android",
                ["system"] = "android",
                ["systemVersion"] = "11",
                ["platform"] = "android",
                ["appVersion"] = "6.28.0",
                ["ipAddress"] = "string",
            },
            cancellationToken).ConfigureAwait(false);

        JsonElement root = response.RootElement;
        return new DeviceLoginStart(
            root.GetProperty("code").GetString() ?? throw new InvalidOperationException("10 Play did not supply a device code."),
            new Uri(ActivateUrl),
            root.GetProperty("expiry").GetInt64());
    }

    /// <summary>Gets the provider's Featured collections for the configured Australian state.</summary>
    public static async Task<JsonDocument> GetFeaturedAsync(CancellationToken cancellationToken)
    {
        using JsonDocument config = await GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        string homepageEndpoint = config.RootElement.GetProperty("homePageApiEndpoint").GetString()
            ?? throw new InvalidOperationException("10 Play did not supply a home-page endpoint.");
        string locationEndpoint = config.RootElement.GetProperty("geoLocationEndpoint").GetString()
            ?? throw new InvalidOperationException("10 Play did not supply a location endpoint.");
        using JsonDocument location = await GetJsonAsync(locationEndpoint, cancellationToken).ConfigureAwait(false);
        string state = location.RootElement.GetProperty("state").GetString()
            ?? throw new InvalidOperationException("10 Play did not supply a viewing state.");
        return await GetJsonAsync(homepageEndpoint.TrimEnd('/') + "/" + Uri.EscapeDataString(state), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls the device-code endpoint and saves a completed account session into the supplied configuration.</summary>
    public static async Task<DeviceLoginCompletion> CompleteDeviceLoginAsync(PluginConfiguration configuration, string code, long expiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(code);
        try
        {
            using JsonDocument config = await GetJsonAsync(ConfigUrl + "?SystemName=android&manufacturer=nvidia", cancellationToken).ConfigureAwait(false);
            string endpoint = config.RootElement.GetProperty("authConfig").GetProperty("validateCode").GetString()
                ?? throw new InvalidOperationException("10 Play did not supply a device-login validation endpoint.");

            using JsonDocument response = await PostJsonAsync(
                endpoint,
                new Dictionary<string, object>
                {
                    ["code"] = code,
                    ["deviceIdentifier"] = configuration.DeviceIdentifier,
                    ["expiry"] = expiry,
                },
                cancellationToken).ConfigureAwait(false);

            if (!response.RootElement.TryGetProperty("jwt", out JsonElement jwt))
            {
                return new DeviceLoginCompletion(false, FindProviderMessage(response.RootElement) ?? "10 Play has not issued a login session yet.");
            }

            SaveTokens(configuration, jwt);
            return new DeviceLoginCompletion(true, null);
        }
        catch (HttpRequestException exception)
        {
            string status = exception.StatusCode is null
                ? "could not be reached"
                : string.Format(CultureInfo.InvariantCulture, "returned HTTP {0}", (int)exception.StatusCode.Value);
            return new DeviceLoginCompletion(false, "10 Play " + status + ".");
        }
    }

    private static Task<JsonDocument> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        return GetJsonAsync(ConfigUrl + "?SystemName=android&manufacturer=nvidia", cancellationToken);
    }

    private static void SaveTokens(PluginConfiguration configuration, JsonElement token)
    {
        string accessToken = token.GetProperty("alternativeToken").GetString() ?? string.Empty;
        string refreshToken = token.GetProperty("refreshToken").GetString() ?? string.Empty;
        JsonElement expiry = token.GetProperty("expiresIn");
        decimal seconds;
        bool valid = expiry.ValueKind == JsonValueKind.Number
            ? expiry.TryGetDecimal(out seconds)
            : decimal.TryParse(expiry.ValueKind == JsonValueKind.String ? expiry.GetString() : null, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!valid || seconds <= 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() - now
            || string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("10 Play returned an invalid account session or token expiry.");
        }

        long expiresAt = now + (long)decimal.Floor(seconds) - 30;
        configuration.AccessToken = accessToken;
        configuration.RefreshToken = refreshToken;
        configuration.TokenExpiresAtUnixSeconds = expiresAt;
    }

    private static string? FindProviderMessage(JsonElement response)
    {
        foreach (string property in new[] { "title", "message", "error", "status" })
        {
            if (response.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("tp-acceptfeature", "v1/fw;v1/drm;v2/live");
        request.Headers.TryAddWithoutValidation("tp-platform", "UAP");
        request.Headers.TryAddWithoutValidation("X-N10-SIG", CreateSignature(url));
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("tp-acceptfeature", "v1/fw;v1/drm;v2/live");
        request.Headers.TryAddWithoutValidation("tp-platform", "UAP");
        request.Headers.TryAddWithoutValidation("X-Network-Ten-Auth", CreateNetworkTenAuth());
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string CreateSignature(string url)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] key = Convert.FromHexString(SignatureKeyHex);
        byte[] message = Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, "{0}:{1}", timestamp, url));
        byte[] signature = HMACSHA256.HashData(key, message);
        return string.Format(CultureInfo.InvariantCulture, "{0}_{1}", timestamp, Convert.ToHexStringLower(signature));
    }

    private static string CreateNetworkTenAuth()
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(timestamp));
    }

    private static HttpClientHandler CreateHandler()
    {
        return new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            CheckCertificateRevocationList = true,
        };
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Linux; Android 11; SHIELD Android TV; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/136.0.7103.125 Mobile Safari/537.36 10play/6.28.0 UAP");
        return client;
    }
}

/// <summary>A device-code login request returned to the dashboard.</summary>
public sealed record DeviceLoginStart(string Code, Uri ActivationUrl, long ExpiresAtUnixSeconds);

/// <summary>The outcome of a device-login confirmation request.</summary>
public sealed record DeviceLoginCompletion(bool Complete, string? Detail);
