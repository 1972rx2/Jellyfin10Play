using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.TenPlay.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TenPlay;

/// <summary>Registers the 10 Play channel and its dashboard settings.</summary>
public sealed class TenPlayPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    public TenPlayPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "10 Play";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("54de03a2-5a75-46e9-9d05-80d210ce3896");

    /// <summary>Gets the running plugin instance.</summary>
    public static TenPlayPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
