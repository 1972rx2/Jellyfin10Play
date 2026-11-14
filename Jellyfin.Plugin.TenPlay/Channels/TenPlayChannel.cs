using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TenPlay.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.TenPlay.Channels;

/// <summary>
/// The browseable 10 Play channel. Content retrieval is added separately from the channel contract so
/// the plugin remains compatible with Jellyfin's supported Channel API.
/// </summary>
public sealed class TenPlayChannel : IChannel, IRequiresMediaInfoCallback
{
    /// <inheritdoc />
    public string Name => "10 Play";

    /// <inheritdoc />
    public string Description => "Australian catch-up TV from 10 Play. A 10 Play account is required for playback.";

    /// <inheritdoc />
    public string HomePageUrl => "https://10.com.au/";

    /// <inheritdoc />
    public string DataVersion => "5";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => TenPlayPlugin.Instance?.Configuration.AccessToken.Length > 0;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Episode, ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            DefaultSortFields = new List<ChannelItemSortField> { ChannelItemSortField.DateCreated, ChannelItemSortField.Name },
        };
    }

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var items = new List<ChannelItemInfo>();
        if (string.IsNullOrEmpty(query.FolderId))
        {
            items.Add(CreateFolder("featured", "Featured"));
            items.Add(CreateFolder("shows", "Shows"));
        }
        else if (string.Equals(query.FolderId, "shows", StringComparison.Ordinal))
        {
            using JsonDocument config = await TenPlayApiClient.GetCatalogueConfigurationAsync(cancellationToken).ConfigureAwait(false);
            items = ParseShowCategories(config.RootElement);
        }
        else if (query.FolderId.StartsWith("category:", StringComparison.Ordinal))
        {
            using JsonDocument document = await TenPlayApiClient.GetShowsCategoryAsync(DecodeId(query.FolderId["category:".Length..]), cancellationToken).ConfigureAwait(false);
            items = ParseRows(document.RootElement.GetProperty("items"));
        }
        else if (string.Equals(query.FolderId, "featured", StringComparison.Ordinal))
        {
            items = await GetFeaturedCollectionsAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (query.FolderId.StartsWith("show:", StringComparison.Ordinal))
        {
            string showId = query.FolderId["show:".Length..];
            using JsonDocument document = await TenPlayApiClient.GetShowAsync(showId, cancellationToken).ConfigureAwait(false);
            JsonElement show = document.RootElement[0];
            foreach (JsonElement season in show.GetProperty("seasons").EnumerateArray())
            {
                bool hasEpisodes = season.GetProperty("menuItems").EnumerateArray().Any(menu =>
                    menu.GetProperty("menuTitle").GetString() == "Episodes" && menu.GetProperty("numberOfEpisodes").GetInt32() > 0);
                if (hasEpisodes)
                {
                    items.Add(CreateFolder("season:" + EncodeId(showId) + ":" + EncodeId(season.GetProperty("id").GetString()!), "Season " + season.GetProperty("title").GetString()));
                }
            }
        }
        else if (query.FolderId.StartsWith("season:", StringComparison.Ordinal))
        {
            string[] parts = query.FolderId.Split(':');
            using JsonDocument document = await TenPlayApiClient.GetSeasonAsync(DecodeId(parts[1]), DecodeId(parts[2]), cancellationToken).ConfigureAwait(false);
            items = ParseRows(document.RootElement);
        }
        else if (query.FolderId.StartsWith("featured:", StringComparison.Ordinal))
        {
            string title = DecodeId(query.FolderId["featured:".Length..]);
            items = await GetFeaturedItemsAsync(title, cancellationToken).ConfigureAwait(false);
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        };
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!id.StartsWith("video:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported 10 Play item.", nameof(id));
        }

        string manifest = await TenPlayApiClient.GetPlaybackAsync(id["video:".Length..], cancellationToken).ConfigureAwait(false);
        return [new MediaSourceInfo
        {
            Id = id,
            Path = manifest,
            Protocol = MediaProtocol.Http,
            Container = "m3u8",
            IsRemote = true,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiredHttpHeaders = new Dictionary<string, string> { ["User-Agent"] = TenPlayApiClient.StreamUserAgent },
        }];
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            throw new ArgumentException("Only the primary channel image is available.", nameof(type));
        }

        Stream? stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.TenPlay.Images.logo.png");
        return Task.FromResult(new DynamicImageResponse
        {
            HasImage = stream is not null,
            Stream = stream,
            Format = ImageFormat.Png,
        });
    }

    private static ChannelItemInfo CreateFolder(string id, string name)
    {
        return new ChannelItemInfo
        {
            Id = id,
            Name = name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
        };
    }

    private static async Task<List<ChannelItemInfo>> GetFeaturedCollectionsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await TenPlayApiClient.GetFeaturedAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>();
        foreach (JsonElement row in document.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("type", out JsonElement type)
                || (type.GetString() is not ("Standard" or "Poster"))
                || !row.TryGetProperty("title", out JsonElement title)
                || string.IsNullOrWhiteSpace(title.GetString()))
            {
                continue;
            }

            string label = title.GetString()!;
            items.Add(CreateFeaturedFolder(label));
        }

        return items;
    }

    private static ChannelItemInfo CreateFeaturedFolder(string name)
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(TenPlayChannel).Assembly.Location) ?? AppContext.BaseDirectory;
        string tileDirectory = Path.Combine(assemblyDirectory, "Images", "FeaturedTiles");
        string tileFileName;
        if (name.StartsWith("Watch the Book, Read", StringComparison.OrdinalIgnoreCase))
        {
            tileFileName = "watch-the-book-read.png";
        }
        else
        {
            tileFileName = FeaturedTileFiles.TryGetValue(name.Trim(), out string? fileName)
                ? fileName
                : "featured-default.png";
        }

        return new ChannelItemInfo
        {
            Id = "featured:" + EncodeId(name),
            Name = name,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            ImageUrl = Path.Combine(tileDirectory, tileFileName),
        };
    }

    private static readonly Dictionary<string, string> FeaturedTileFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["All the Drama"] = "all-the-drama.png",
        ["Animation Station"] = "animation-station.png",
        ["Comic Relief"] = "comic-relief.png",
        ["Deep Dive Docs"] = "deep-dive-docs.png",
        ["Everyday Inspiration"] = "everyday-inspiration.png",
        ["Fair Dinkum Funny"] = "fair-dinkum-funny.png",
        ["From the Silver Screen"] = "from-the-silver-screen.png",
        ["Is That Your Final Answer?"] = "is-that-your-final-answer.png",
        ["Jamie's Kitchen"] = "jamies-kitchen.png",
        ["Newly Arrived from the UK"] = "newly-arrived-from-the-uk.png",
        ["One Mic, No Filter"] = "one-mic-no-filter.png",
        ["Real Cases, Real Consequences"] = "real-cases-real-consequences.png",
        ["Real People, Real Chaos"] = "real-people-real-chaos.png",
        ["Recently Added"] = "recently-added.png",
        ["Recommended For You"] = "recommended-for-you.png",
        ["Ride the Korean Wave"] = "ride-the-korean-wave.png",
        ["Royals Revealed"] = "royals-revealed.png",
        ["Sitcom Central"] = "sitcom-central.png",
        ["Soaps and Nostalgia"] = "soaps-and-nostalgia.png",
        ["Strange and Otherworldly"] = "strange-and-otherworldly.png",
        ["Thrillers and Killers"] = "thrillers-and-killers.png",
        ["The Traitors"] = "the-traitors.png",
        ["Where to Next?"] = "where-to-next.png",
    };

    private static async Task<List<ChannelItemInfo>> GetFeaturedItemsAsync(string selectedTitle, CancellationToken cancellationToken)
    {
        using JsonDocument document = await TenPlayApiClient.GetFeaturedAsync(cancellationToken).ConfigureAwait(false);
        JsonElement selected = document.RootElement.EnumerateArray().FirstOrDefault(row =>
            row.TryGetProperty("title", out JsonElement title)
            && string.Equals(title.GetString(), selectedTitle, StringComparison.Ordinal));

        if (selected.ValueKind is not JsonValueKind.Object || !selected.TryGetProperty("items", out JsonElement rows))
        {
            return [];
        }

        return ParseRows(rows);
    }

    private static List<ChannelItemInfo> ParseRows(JsonElement rows)
    {
        var items = new List<ChannelItemInfo>();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("id", out JsonElement id) || string.IsNullOrWhiteSpace(id.GetString()))
            {
                continue;
            }

            string type = row.TryGetProperty("type", out JsonElement itemType) ? itemType.GetString() ?? string.Empty : string.Empty;
            string title = row.TryGetProperty("title", out JsonElement itemTitle) ? itemTitle.GetString() ?? "Untitled" : "Untitled";
            string? image = row.TryGetProperty("imageUrl", out JsonElement imageUrl) ? imageUrl.GetString() : null;
            string? overview = row.TryGetProperty("description", out JsonElement description) ? description.GetString() : null;

            if (string.Equals(type, "Show", StringComparison.Ordinal) && row.TryGetProperty("numberOfEpisodes", out JsonElement episodeCount) && episodeCount.GetInt32() > 0)
            {
                items.Add(new ChannelItemInfo
                {
                    Id = "show:" + id.GetString(),
                    Name = title,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    ImageUrl = image,
                    Overview = overview,
                });
            }
            else if (string.Equals(type, "Video", StringComparison.Ordinal))
            {
                items.Add(new ChannelItemInfo
                {
                    Id = "video:" + id.GetString(),
                    Name = title,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Episode,
                    ImageUrl = image,
                    Overview = overview,
                });
            }
        }

        return items;
    }

    private static List<ChannelItemInfo> ParseShowCategories(JsonElement configuration)
    {
        var items = new List<ChannelItemInfo>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement menu in configuration.GetProperty("menuConfig").GetProperty("primary").EnumerateArray())
        {
            if (!menu.TryGetProperty("type", out JsonElement type) || type.GetString() != "Shows"
                || !menu.TryGetProperty("subItems", out JsonElement categories))
            {
                continue;
            }

            foreach (JsonElement category in categories.EnumerateArray())
            {
                string? label = category.TryGetProperty("label", out JsonElement name) ? name.GetString() : null;
                if (!string.IsNullOrWhiteSpace(label) && category.TryGetProperty("apiEndpoint", out JsonElement endpoint)
                    && !string.IsNullOrWhiteSpace(endpoint.GetString()) && labels.Add(label))
                {
                    items.Add(CreateFolder("category:" + EncodeId(label), label));
                }
            }
        }

        return items;
    }

    private static string EncodeId(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodeId(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
