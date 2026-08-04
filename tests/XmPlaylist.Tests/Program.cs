using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser.Model;
using XmPlaylist.ImportLists;

internal static class Program
{
    private static int _failures;

    public static void Main()
    {
        Console.WriteLine("=== XmPlaylist Parser Tests ===");

        TestArtistOnlyEmission();
        TestHistoryDedupeAcrossFetches();
        TestMultiArtistPlayRecordsEachArtist();
        TestHistoryStorePersistsAcrossInstances();
        TestBackfillStopsAtCutoff();
        TestBackfillStopsAtMaxPages();
        TestAlbumResolutionViaDeezerAndMusicBrainz();
        TestAlbumResolutionFallsBackToAppleMusic();
        TestAlbumResolutionIsCachedPerTrack();
        TestAlbumResolutionSkippedForMultiArtistPlays();
        TestAlbumResolutionFallsBackToDeezerTitleWithoutMbid();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    private static void TestArtistOnlyEmission()
    {
        Console.WriteLine("\n[Test] Every play emits one artist-only item");

        var store = NewHistoryStore();
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store };
        var feed = BuildFeed(("Artist One", "Song A"));

        var items = parser.ParseResponse(feed);

        Assert($"emits 1 item (got {items.Count})", items.Count == 1);
        Assert("item has no album set", items[0].Album.IsNullOrWhiteSpace());
        Assert("item has the right artist", items[0].Artist == "Artist One");
    }

    private static void TestHistoryDedupeAcrossFetches()
    {
        Console.WriteLine("\n[Test] Re-fetching an overlapping window doesn't re-emit the same play");

        var store = NewHistoryStore();
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store };

        // Same play id both times, simulating overlapping backfill windows between polls.
        var feed = BuildFeed(("playA", "Artist One", "Song A"));

        var first = parser.ParseResponse(feed);
        var second = parser.ParseResponse(feed);

        Assert($"first fetch emits the play (got {first.Count})", first.Count == 1);
        Assert($"second fetch emits nothing (got {second.Count})", second.Count == 0);
    }

    private static void TestMultiArtistPlayRecordsEachArtist()
    {
        Console.WriteLine("\n[Test] A play with multiple credited artists records/emits each independently");

        var store = NewHistoryStore();
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store };

        var entry = "{\"id\":\"playB\",\"timestamp\":\"2026-08-04T00:00:00Z\"," +
                    "\"track\":{\"id\":\"T\",\"title\":\"Collab Song\",\"artists\":[\"Artist X\",\"Artist Y\"]}," +
                    "\"channelId\":\"altnation\"}";
        var feed = BuildFeedFromEntries(entry);

        var items = parser.ParseResponse(feed);

        Assert($"emits one item per credited artist (got {items.Count})", items.Count == 2);
        Assert("includes Artist X", ContainsArtist(items, "Artist X"));
        Assert("includes Artist Y", ContainsArtist(items, "Artist Y"));
    }

    private static void TestHistoryStorePersistsAcrossInstances()
    {
        Console.WriteLine("\n[Test] History survives across parser/store instances (on disk)");

        var appFolder = new FakeAppFolderInfo(Path.Combine(Path.GetTempPath(), "xmplaylist-test-" + Guid.NewGuid()));

        var store1 = new XmPlaylistHistoryStore(appFolder);
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser1 = new XmPlaylistParser { Settings = settings, HistoryStore = store1 };
        var feed = BuildFeed(("playC", "Artist Persisted", "Song A"));
        parser1.ParseResponse(feed);

        var store2 = new XmPlaylistHistoryStore(appFolder);
        var parser2 = new XmPlaylistParser { Settings = settings, HistoryStore = store2 };
        var second = parser2.ParseResponse(feed);

        Assert($"new store instance on the same folder sees prior history (got {second.Count})", second.Count == 0);
    }

    private static void TestBackfillStopsAtCutoff()
    {
        Console.WriteLine("\n[Test] Station backfill stops once a page's oldest play crosses the poll window");

        var now = DateTime.UtcNow;
        var pages = new Dictionary<string, HttpResponse>
        {
            ["page1"] = BuildPage(new[] { now.AddMinutes(-5), now.AddMinutes(-10) }, "page2"),
            ["page2"] = BuildPage(new[] { now.AddMinutes(-15), now.AddMinutes(-400) }, "page3"),
            ["page3"] = BuildPage(new[] { now.AddMinutes(-410) }, null)
        };

        var fetchCount = 0;
        Func<HttpRequest, HttpResponse> fetchPage = req =>
        {
            fetchCount++;
            var key = req.Url.FullUri.Contains("page2") ? "page2" : req.Url.FullUri.Contains("page3") ? "page3" : "page1";
            return pages[key];
        };

        var request = BuildRequest("https://xmplaylist.com/api/station/altnation");
        var response = XmPlaylistStationBackfill.Fetch(request, TimeSpan.FromHours(6), fetchPage);

        Assert($"stops after page 2, not fetching page 3 (fetched {fetchCount} pages)", fetchCount == 2);
        Assert("merges results from both fetched pages", response.HttpResponse.Content.Contains("\"count\":4"));
    }

    private static void TestBackfillStopsAtMaxPages()
    {
        Console.WriteLine("\n[Test] Station backfill has a safety cap on page count");

        var now = DateTime.UtcNow;
        var fetchCount = 0;
        Func<HttpRequest, HttpResponse> fetchPage = req =>
        {
            fetchCount++;
            return BuildPage(new[] { now.AddMinutes(-fetchCount) }, "next-page");
        };

        var request = BuildRequest("https://xmplaylist.com/api/station/altnation");
        XmPlaylistStationBackfill.Fetch(request, TimeSpan.FromDays(30), fetchPage);

        Assert($"never exceeds MaxPages (fetched {fetchCount}, cap {XmPlaylistStationBackfill.MaxPages})", fetchCount == XmPlaylistStationBackfill.MaxPages);
    }

    private static void TestAlbumResolutionViaDeezerAndMusicBrainz()
    {
        Console.WriteLine("\n[Test] Album resolves via Deezer ISRC -> MusicBrainz release-group");

        var store = NewHistoryStore();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store, AlbumResolver = resolver };

        var links = new (string Site, string Url)[] { ("deezer", "https://www.deezer.com/track/624510") };
        var feed = BuildFeedFromEntries(BuildEntry("playD", "trackD", "Artist One", "I'm Open", links));

        var items = parser.ParseResponse(feed);

        Assert($"emits 1 item (got {items.Count})", items.Count == 1);
        Assert("album resolved to real title", items[0].Album == "No Code");
        Assert("album MBID attached", items[0].AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached (single-artist play)", items[0].ArtistMusicBrainzId == "artist-mbid-1");
        Assert("Deezer, ISRC, and recording endpoints were each called once", httpClient.CallCount == 3);
    }

    private static void TestAlbumResolutionFallsBackToAppleMusic()
    {
        Console.WriteLine("\n[Test] Falls back to Apple Music lookup when there's no Deezer link");

        var store = NewHistoryStore();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"No Code\"}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store, AlbumResolver = resolver };

        var links = new (string Site, string Url)[] { ("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507") };
        var feed = BuildFeedFromEntries(BuildEntry("playE", "trackE", "Artist One", "I'm Open", links));

        var items = parser.ParseResponse(feed);

        Assert($"emits 1 item (got {items.Count})", items.Count == 1);
        Assert("album resolved via Apple fallback", items[0].Album == "No Code");
        Assert("no album MBID (Apple path doesn't have one)", items[0].AlbumMusicBrainzId.IsNullOrWhiteSpace());
    }

    private static void TestAlbumResolutionIsCachedPerTrack()
    {
        Console.WriteLine("\n[Test] Album resolution is cached per track id, not repeated on replay");

        var store = NewHistoryStore();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store, AlbumResolver = resolver };
        var links = new (string Site, string Url)[] { ("deezer", "https://www.deezer.com/track/624510") };

        // Same track id (song replayed), different play ids - simulates the song airing twice.
        var first = parser.ParseResponse(BuildFeedFromEntries(BuildEntry("playF1", "trackF", "Artist One", "I'm Open", links)));
        var callsAfterFirst = httpClient.CallCount;
        var second = parser.ParseResponse(BuildFeedFromEntries(BuildEntry("playF2", "trackF", "Artist One", "I'm Open", links)));

        Assert($"first play resolves the album (got '{first[0].Album}')", first[0].Album == "No Code");
        Assert($"second play (same track id) reuses the cached album (got '{second[0].Album}')", second[0].Album == "No Code");
        Assert($"no extra HTTP calls made for the second play (before {callsAfterFirst}, after {httpClient.CallCount})", httpClient.CallCount == callsAfterFirst);
    }

    private static void TestAlbumResolutionSkippedForMultiArtistPlays()
    {
        Console.WriteLine("\n[Test] Multi-artist plays get the album but not a borrowed artist MBID");

        var store = NewHistoryStore();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist X\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"Collab Album\",\"primary-type\":\"Album\",\"first-release-date\":\"2020-01-01\"}}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store, AlbumResolver = resolver };
        var links = new (string Site, string Url)[] { ("deezer", "https://www.deezer.com/track/624510") };

        var entry = "{\"id\":\"playG\",\"timestamp\":\"2026-08-04T00:00:00Z\"," +
                    "\"track\":{\"id\":\"trackG\",\"title\":\"Collab Song\",\"artists\":[\"Artist X\",\"Artist Y\"]}," +
                    "\"channelId\":\"altnation\"," + LinksJson(links) + "}";

        var items = parser.ParseResponse(BuildFeedFromEntries(entry));

        Assert($"emits one item per credited artist (got {items.Count})", items.Count == 2);
        foreach (var item in items)
        {
            Assert($"{item.Artist} gets the resolved album", item.Album == "Collab Album");
            Assert($"{item.Artist} does not get a borrowed artist MBID", item.ArtistMusicBrainzId.IsNullOrWhiteSpace());
        }
    }

    private static void TestAlbumResolutionFallsBackToDeezerTitleWithoutMbid()
    {
        Console.WriteLine("\n[Test] When MusicBrainz can't resolve an ISRC match, Deezer's own album title is used instead of Apple");

        var store = NewHistoryStore();
        var httpClient = new FakeHttpClient();

        // No "isrc" field at all - the MusicBrainz path bails out immediately, but Deezer's own
        // album title is still sitting right there in the same response.
        httpClient.Respond("api.deezer.com/track/624510", "{\"album\":{\"title\":\"No Code\"}}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var settings = new XmPlaylistImportSettings { Channel = "altnation" };
        var parser = new XmPlaylistParser { Settings = settings, HistoryStore = store, AlbumResolver = resolver };

        var links = new (string Site, string Url)[] { ("deezer", "https://www.deezer.com/track/624510") };
        var feed = BuildFeedFromEntries(BuildEntry("playH", "trackH", "Artist One", "I'm Open", links));

        var items = parser.ParseResponse(feed);

        Assert($"emits 1 item (got {items.Count})", items.Count == 1);
        Assert("falls back to Deezer's own album title", items[0].Album == "No Code");
        Assert("no album MBID (no MusicBrainz match)", items[0].AlbumMusicBrainzId.IsNullOrWhiteSpace());
        Assert($"only the single Deezer call was made, no Apple fallback needed (calls: {httpClient.CallCount})", httpClient.CallCount == 1);
    }

    private static string BuildEntry(string playId, string trackId, string artist, string title, (string Site, string Url)[] links)
    {
        return "{\"id\":\"" + playId + "\",\"timestamp\":\"2026-08-04T00:00:00Z\"," +
               "\"track\":{\"id\":\"" + trackId + "\",\"title\":\"" + title + "\",\"artists\":[\"" + artist + "\"]}," +
               "\"channelId\":\"altnation\"," + LinksJson(links) + "}";
    }

    private static string LinksJson((string Site, string Url)[] links)
    {
        var entries = new List<string>();
        foreach (var link in links)
        {
            entries.Add($"{{\"site\":\"{link.Site}\",\"url\":\"{link.Url}\"}}");
        }

        return "\"links\":[" + string.Join(",", entries) + "]";
    }

    private static XmPlaylistHistoryStore NewHistoryStore()
    {
        var folder = Path.Combine(Path.GetTempPath(), "xmplaylist-test-" + Guid.NewGuid());
        return new XmPlaylistHistoryStore(new FakeAppFolderInfo(folder));
    }

    private static HttpResponse BuildPage(DateTime[] timestamps, string? nextCursorMarker)
    {
        var entries = new List<string>();
        foreach (var ts in timestamps)
        {
            entries.Add($"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":\"{ts:yyyy-MM-ddTHH:mm:ss.fffZ}\",\"track\":{{\"id\":\"T\",\"title\":\"Song\",\"artists\":[\"Artist\"]}},\"channelId\":\"altnation\"}}");
        }

        var next = nextCursorMarker == null ? "null" : $"\"https://xmplaylist.com/api/station/altnation?last={nextCursorMarker}\"";
        var json = "{\"count\":" + entries.Count + ",\"next\":" + next + ",\"previous\":null,\"results\":[" + string.Join(",", entries) + "]}";

        var request = BuildRequest("https://xmplaylist.com/api/station/altnation");
        return new HttpResponse(request.HttpRequest, new HttpHeader(), json, HttpStatusCode.OK);
    }

    private static ImportListRequest BuildRequest(string url)
    {
        var request = new HttpRequest(url, HttpAccept.Json);
        return new ImportListRequest(request);
    }

    private static ImportListResponse BuildFeed(params (string Artist, string Title)[] plays)
    {
        var entries = new List<string>();
        foreach (var play in plays)
        {
            entries.Add($"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":\"2026-08-04T00:00:00Z\",\"track\":{{\"id\":\"T\",\"title\":\"{play.Title}\",\"artists\":[\"{play.Artist}\"]}},\"channelId\":\"altnation\"}}");
        }

        return BuildFeedFromEntries(entries.ToArray());
    }

    private static ImportListResponse BuildFeed(params (string PlayId, string Artist, string Title)[] plays)
    {
        var entries = new List<string>();
        foreach (var play in plays)
        {
            entries.Add($"{{\"id\":\"{play.PlayId}\",\"timestamp\":\"2026-08-04T00:00:00Z\",\"track\":{{\"id\":\"T\",\"title\":\"{play.Title}\",\"artists\":[\"{play.Artist}\"]}},\"channelId\":\"altnation\"}}");
        }

        return BuildFeedFromEntries(entries.ToArray());
    }

    private static ImportListResponse BuildFeedFromEntries(params string[] entries)
    {
        var json = "{\"count\":" + entries.Length + ",\"next\":null,\"previous\":null,\"results\":[" + string.Join(",", entries) + "]}";

        var request = new HttpRequest("https://xmplaylist.com/api/station/altnation", HttpAccept.Json);
        var importRequest = new ImportListRequest(request);
        var httpResponse = new HttpResponse(importRequest.HttpRequest, new HttpHeader(), json, HttpStatusCode.OK);
        return new ImportListResponse(importRequest, httpResponse);
    }

    private static bool ContainsArtist(IList<ImportListItemInfo> items, string artist)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Artist, artist, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void Assert(string description, bool condition)
    {
        Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {description}");
        if (!condition)
        {
            _failures++;
        }
    }
}

internal class FakeHttpClient : IHttpClient
{
    private readonly Dictionary<string, string> _responsesByUrlFragment = new();

    public int CallCount { get; private set; }

    public void Respond(string urlFragment, string jsonContent)
    {
        _responsesByUrlFragment[urlFragment] = jsonContent;
    }

    public HttpResponse Get(HttpRequest request)
    {
        CallCount++;

        foreach (var pair in _responsesByUrlFragment)
        {
            if (request.Url.FullUri.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponse(request, new HttpHeader(), pair.Value, HttpStatusCode.OK);
            }
        }

        return new HttpResponse(request, new HttpHeader(), "{}", HttpStatusCode.NotFound);
    }

    public HttpResponse Execute(HttpRequest request) => Get(request);
    public void DownloadFile(string url, string fileName) => throw new NotSupportedException();
    public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
    public HttpResponse Head(HttpRequest request) => throw new NotSupportedException();
    public HttpResponse Post(HttpRequest request) => throw new NotSupportedException();
    public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotSupportedException();
    public System.Threading.Tasks.Task DownloadFileAsync(string url, string fileName) => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotSupportedException();
    public System.Threading.Tasks.Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotSupportedException();
}

internal class FakeAppFolderInfo : IAppFolderInfo
{
    public FakeAppFolderInfo(string appDataFolder)
    {
        AppDataFolder = appDataFolder;
    }

    public string AppDataFolder { get; }
    public string TempFolder => Path.GetTempPath();
    public string StartUpFolder => AppDomain.CurrentDomain.BaseDirectory;
}
