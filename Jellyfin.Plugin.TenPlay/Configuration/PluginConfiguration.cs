using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TenPlay.Configuration;

/// <summary>Stores the local 10 Play device-login session.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Initializes a new instance of the <see cref="PluginConfiguration"/> class.</summary>
    public PluginConfiguration()
    {
        DeviceIdentifier = Guid.NewGuid().ToString("N");
    }

    /// <summary>Gets or sets the stable device identifier used for official device-code login.</summary>
    public string DeviceIdentifier { get; set; }

    /// <summary>Gets or sets the provider access token. This is stored locally in Jellyfin's plugin configuration.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider refresh token. This is stored locally in Jellyfin's plugin configuration.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the token expiry as Unix time in seconds.</summary>
    public long TokenExpiresAtUnixSeconds { get; set; }

    /// <summary>Gets or sets whether live channels appear in the channel root.</summary>
    public bool IncludeLiveTv { get; set; } = true;
}
