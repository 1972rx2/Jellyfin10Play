using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.TenPlay.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

using var fixture = JsonDocument.Parse("""
[
 {"id":"show1","type":"Show","title":"Test Show","numberOfEpisodes":2,"imageUrl":"https://example.com/show.jpg"},
 {"id":"episode1","type":"Video","title":"Episode 1","description":"Test episode","imageUrl":"https://example.com/episode.jpg"},
 {"id":"empty","type":"Show","title":"Empty Show","numberOfEpisodes":0}
]
""");
var parse = typeof(TenPlayChannel).GetMethod("ParseRows", BindingFlags.NonPublic | BindingFlags.Static)!;
var items = (List<ChannelItemInfo>)parse.Invoke(null, new object[] { fixture.RootElement })!;
if (items.Count != 2 || items[0].Id != "show:show1" || items[0].Type != ChannelItemType.Folder
    || items[1].Id != "video:episode1" || items[1].Type != ChannelItemType.Media
    || items[1].ContentType != ChannelMediaContentType.Episode)
    throw new Exception("Show/episode mapping failed.");
IRequiresMediaInfoCallback channel = new TenPlayChannel();
try { await channel.GetChannelItemMediaInfo("show:show1", default); throw new Exception("Folder accepted for playback."); }
catch (ArgumentException) { }
var root = await ((IChannel)channel).GetChannelItems(new InternalChannelItemQuery(), default);
if (root.Items.Count != 2 || root.Items[0].Id != "featured" || root.Items[1].Id != "shows") throw new Exception("Catalogue root folders missing.");
Console.WriteLine("PASS: show/episode mapping, playback callback guard, and implemented root folders.");

var saveTokens = typeof(Jellyfin.Plugin.TenPlay.Services.TenPlayApiClient).GetMethod("SaveTokens", BindingFlags.NonPublic | BindingFlags.Static)!;
foreach (string expiry in new[] { "3600", "3600.75", "\"3600\"", "\"3600.75\"" })
{
    var configuration = new Jellyfin.Plugin.TenPlay.Configuration.PluginConfiguration();
    using var tokens = JsonDocument.Parse("{\"alternativeToken\":\"test-access\",\"refreshToken\":\"test-refresh\",\"expiresIn\":" + expiry + "}");
    long before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    saveTokens.Invoke(null, new object[] { configuration, tokens.RootElement });
    long after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (configuration.AccessToken != "test-access" || configuration.RefreshToken != "test-refresh"
        || configuration.TokenExpiresAtUnixSeconds < before + 3570 || configuration.TokenExpiresAtUnixSeconds > after + 3570)
        throw new Exception("Token expiry parsing failed: " + expiry);
}
foreach (string expiry in new[] { "null", "-1", "\"invalid\"", "1e30" })
{
    var configuration = new Jellyfin.Plugin.TenPlay.Configuration.PluginConfiguration
    { AccessToken = "original-access", RefreshToken = "original-refresh", TokenExpiresAtUnixSeconds = 123 };
    using var tokens = JsonDocument.Parse("{\"alternativeToken\":\"new-access\",\"refreshToken\":\"new-refresh\",\"expiresIn\":" + expiry + "}");
    try { saveTokens.Invoke(null, new object[] { configuration, tokens.RootElement }); throw new Exception("Invalid token expiry accepted."); }
    catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException) { }
    if (configuration.AccessToken != "original-access" || configuration.RefreshToken != "original-refresh" || configuration.TokenExpiresAtUnixSeconds != 123)
        throw new Exception("Failed parsing changed the saved session.");
}
Console.WriteLine("PASS: integer, decimal, text token expiry and atomic failure handling.");

var parseCategories = typeof(TenPlayChannel).GetMethod("ParseShowCategories", BindingFlags.NonPublic | BindingFlags.Static)!;
using var categoryFixture = JsonDocument.Parse("""
{"menuConfig":{"primary":[
 {"type":"Other","subItems":[{"label":"Wrong","apiEndpoint":"https://example.com/wrong"}]},
 {"type":"Shows","subItems":[
  {"label":"All Shows","apiEndpoint":"https://example.com/all"},
  {"label":"Reality & Drama","apiEndpoint":"https://example.com/reality"},
  {"label":"All Shows","apiEndpoint":"https://example.com/duplicate"},
  {"label":"Missing endpoint"}]}]}}
""");
var categories = (List<ChannelItemInfo>)parseCategories.Invoke(null, new object[] { categoryFixture.RootElement })!;
if (categories.Count != 2 || categories[0].Name != "All Shows"
    || System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(categories[1].Id["category:".Length..])) != "Reality & Drama")
    throw new Exception("Catalogue category mapping failed.");
Console.WriteLine("PASS: Shows categories, duplicate filtering, and category ID roundtrip.");

if (args.Contains("--live"))
{
    using var config = await Jellyfin.Plugin.TenPlay.Services.TenPlayApiClient.GetCatalogueConfigurationAsync(default);
    var liveCategories = (List<ChannelItemInfo>)parseCategories.Invoke(null, new object[] { config.RootElement })!;
    foreach (var category in liveCategories)
    {
        using var catalogue = await Jellyfin.Plugin.TenPlay.Services.TenPlayApiClient.GetShowsCategoryAsync(category.Name, default);
        var shows = (List<ChannelItemInfo>)parse.Invoke(null, new object[] { catalogue.RootElement.GetProperty("items") })!;
        Console.WriteLine($"LIVE: {category.Name}: {shows.Count} entries; Big Brother: {string.Join(", ", shows.Where(show => show.Name.Contains("Big Brother", StringComparison.OrdinalIgnoreCase)).Select(show => show.Name))}");
        foreach (var bigBrother in shows.Where(show => show.Name == "Big Brother"))
        {
            string showId = bigBrother.Id["show:".Length..];
            using var showDocument = await Jellyfin.Plugin.TenPlay.Services.TenPlayApiClient.GetShowAsync(showId, default);
            foreach (var season in showDocument.RootElement[0].GetProperty("seasons").EnumerateArray())
            {
                if (!season.GetProperty("menuItems").EnumerateArray().Any(menu => menu.GetProperty("menuTitle").GetString() == "Episodes" && menu.GetProperty("numberOfEpisodes").GetInt32() > 0)) continue;
                using var episodeDocument = await Jellyfin.Plugin.TenPlay.Services.TenPlayApiClient.GetSeasonAsync(showId, season.GetProperty("id").GetString()!, default);
                var episodes = (List<ChannelItemInfo>)parse.Invoke(null, new object[] { episodeDocument.RootElement })!;
                Console.WriteLine($"LIVE: Big Brother season {season.GetProperty("title").GetString()}: {episodes.Count} episodes.");
            }
        }
    }
}
