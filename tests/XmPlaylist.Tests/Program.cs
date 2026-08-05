using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Commands;
using NzbDrone.Core.Parser.Model;
using XmPlaylist.ImportLists;

internal static class Program
{
    private static int _failures;

    public static void Main()
    {
        Console.WriteLine("=== XmPlaylist Tests ===");

        TestBackfillStopsAtCutoff();
        TestBackfillStopsAtMaxPages();
        TestChannelDirectoryFetchesAndParses();
        TestChannelCacheStoresAndReuses();
        TestSettingsRequireNonEmptyChannel();
        TestRefreshSchedulerPushesRefreshForNewlyMonitoredAlbum();

        TestAlbumResolutionViaDeezerAndMusicBrainz();
        TestAlbumResolutionFallsBackToDeezerTitle();
        TestAlbumResolutionFallsBackToAppleMusic();
        TestMusicBrainzBusyIsRetried();
        TestMusicBrainzGivesUpAfterMaxRetries();

        TestStoreRecordsAndDedupesPlays();
        TestStoreUpsertsTrackAndResolvesToPresentable();
        TestStoreThreeStrikesExcludesTrack();
        TestStorePresentableWindowExpires();
        TestStorePruneRemovesOldData();

        TestWorkerCapturesDueChannel();
        TestWorkerSkipsCaptureWhenNotDue();
        TestWorkerResolvesDueTracks();
        TestWorkerIdlesWithNoChannels();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TEST(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
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

    private static void TestChannelDirectoryFetchesAndParses()
    {
        Console.WriteLine("\n[Test] Channel directory parses /api/station into deeplink/name/number");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station", "{\"count\":2,\"results\":[" +
            "{\"deeplink\":\"altnation\",\"name\":\"Alt Nation\",\"number\":\"36\"}," +
            "{\"deeplink\":\"thespectrum\",\"name\":\"The Spectrum\",\"number\":\"28\"}]}");

        var channels = XmPlaylistChannelDirectory.Fetch(httpClient, "https://xmplaylist.com");

        Assert($"parses both channels (got {channels.Count})", channels.Count == 2);
        Assert("has altnation with the right name/number", channels.Any(c => c.Deeplink == "altnation" && c.Name == "Alt Nation" && c.Number == "36"));
    }

    private static void TestChannelCacheStoresAndReuses()
    {
        Console.WriteLine("\n[Test] Channel cache stores and reuses the channel list");

        var store = NewHistoryStore();

        Assert("cache starts empty", store.GetCachedChannels().Count == 0);
        Assert("cache age is null when empty", store.GetChannelCacheAge() == null);

        store.SaveChannels(new[]
        {
            new ChannelInfo("altnation", "Alt Nation", "36"),
            new ChannelInfo("thespectrum", "The Spectrum", "28")
        });

        var cached = store.GetCachedChannels();
        Assert($"cache now has 2 channels (got {cached.Count})", cached.Count == 2);
        Assert("cache age is set after saving", store.GetChannelCacheAge() != null);

        store.SaveChannels(new[] { new ChannelInfo("altnation", "Alt Nation", "36") });
        Assert($"re-saving replaces rather than appends (got {store.GetCachedChannels().Count})", store.GetCachedChannels().Count == 1);
    }

    private static void TestSettingsRequireNonEmptyChannel()
    {
        Console.WriteLine("\n[Test] Settings validation requires a channel to be selected");

        var none = new XmPlaylistImportSettings { Channel = "" };
        var one = new XmPlaylistImportSettings { Channel = "altnation" };

        Assert("empty channel fails validation", !none.Validate().IsValid);
        Assert("a selected channel passes validation", one.Validate().IsValid);
    }

    private static void TestRefreshSchedulerPushesRefreshForNewlyMonitoredAlbum()
    {
        Console.WriteLine("\n[Test] Refresh scheduler pushes RefreshArtistCommand only for an existing artist's newly-monitored album");

        var artists = new FakeArtistService();
        var albums = new FakeAlbumService();
        var queue = new FakeCommandQueue();
        var scheduler = new XmPlaylistRefreshScheduler(artists, albums, queue, LogManager.GetLogger("Test"));

        artists.Add(new Artist { Id = 1, Name = "Kid Sistr", ForeignArtistId = "artist-mbid-1" });
        artists.Add(new Artist { Id = 2, Name = "Various Artists", ForeignArtistId = "va-mbid" });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-1", Monitored = false });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-2", Monitored = true });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-3", Monitored = false });

        var item = (string artistMbid, string albumMbid) => new ImportListItemInfo
        {
            Artist = "someone",
            Album = "some album",
            ArtistMusicBrainzId = artistMbid,
            AlbumMusicBrainzId = albumMbid
        };

        scheduler.Schedule(new List<ImportListItemInfo> { item("artist-mbid-1", "album-mbid-1") });
        Assert($"pushes one refresh command (got {queue.Pushed.Count})", queue.Pushed.Count == 1);
        Assert("targets the right artist id", queue.Pushed[0].ArtistIds.SequenceEqual(new List<int> { 1 }));
        Assert("not marked as a new artist", !queue.Pushed[0].IsNewArtist);

        scheduler.Schedule(new List<ImportListItemInfo> { item("artist-mbid-1", "album-mbid-2") });
        Assert($"no refresh when the album is already monitored (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        scheduler.Schedule(new List<ImportListItemInfo> { item("brand-new-artist", "album-mbid-1") });
        Assert($"no refresh for a brand-new artist (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        scheduler.Schedule(new List<ImportListItemInfo> { item("artist-mbid-1", "brand-new-album") });
        Assert($"no refresh for a brand-new album (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        scheduler.Schedule(new List<ImportListItemInfo> { item("va-mbid", "album-mbid-3") });
        Assert($"no refresh for Various Artists (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        scheduler.Schedule(new List<ImportListItemInfo> { item("", "") });
        Assert($"no refresh when MBIDs are missing (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        artists.Add(new Artist { Id = 5, Name = "Phoenix", ForeignArtistId = "artist-mbid-5" });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-5", Monitored = false });
        scheduler.Schedule(new List<ImportListItemInfo>
        {
            item("artist-mbid-1", "album-mbid-1"),
            item("artist-mbid-5", "album-mbid-5")
        });
        Assert($"second batch pushed one more command (pushed {queue.Pushed.Count})", queue.Pushed.Count == 2);
        Assert("batches distinct artist ids", queue.Pushed[1].ArtistIds.SequenceEqual(new List<int> { 1, 5 }));

        scheduler.Schedule(null);
        Assert($"no command for an empty batch (pushed {queue.Pushed.Count})", queue.Pushed.Count == 2);
    }

    private static Dictionary<string, string> Links(params (string Site, string Url)[] links)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            dict[link.Site] = link.Url;
        }

        return dict;
    }

    private static void TestAlbumResolutionViaDeezerAndMusicBrainz()
    {
        Console.WriteLine("\n[Test] Album resolves via Deezer ISRC -> MusicBrainz release-group");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("resolved to a real title", resolution.Album == "No Code");
        Assert("album MBID attached", resolution.AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached", resolution.ArtistMusicBrainzId == "artist-mbid-1");
        Assert("Deezer, ISRC, and recording endpoints were each called once", httpClient.CallCount == 3);
    }

    private static void TestAlbumResolutionFallsBackToDeezerTitle()
    {
        Console.WriteLine("\n[Test] When MusicBrainz can't resolve an ISRC match, Deezer's own album title is used");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"album\":{\"title\":\"No Code\"}}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("falls back to Deezer's own album title", resolution.Album == "No Code");
        Assert("no album MBID (no MusicBrainz match)", resolution.AlbumMusicBrainzId == null);
        Assert($"only the single Deezer call was made (calls: {httpClient.CallCount})", httpClient.CallCount == 1);
    }

    private static void TestAlbumResolutionFallsBackToAppleMusic()
    {
        Console.WriteLine("\n[Test] Falls back to Apple Music lookup when there's no Deezer link");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"No Code\"}]}");

        var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("resolved via Apple fallback", resolution.Album == "No Code");
        Assert("no album MBID (Apple path doesn't have one)", resolution.AlbumMusicBrainzId == null);
    }

    private static void TestMusicBrainzBusyIsRetried()
    {
        Console.WriteLine("\n[Test] MusicBrainz 503 'busy' is retried instead of failing immediately");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.RespondSequence("musicbrainz.org/ws/2/isrc/USSM19601763",
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.OK, "{\"recordings\":[{\"id\":\"rec-1\"}]}"));
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var originalBackoff = XmPlaylistAlbumResolver.MusicBrainzRetryBackoff;
        XmPlaylistAlbumResolver.MusicBrainzRetryBackoff = TimeSpan.Zero;
        try
        {
            var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
            var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

            Assert("album resolved after retrying the busy MusicBrainz response", resolution.Album == "No Code");
            Assert($"ISRC endpoint hit twice (503 then OK) - total calls {httpClient.CallCount}", httpClient.CallCount == 4);
        }
        finally
        {
            XmPlaylistAlbumResolver.MusicBrainzRetryBackoff = originalBackoff;
        }
    }

    private static void TestMusicBrainzGivesUpAfterMaxRetries()
    {
        Console.WriteLine("\n[Test] MusicBrainz gives up once the retry budget is exhausted");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.RespondSequence("musicbrainz.org/ws/2/isrc/USSM19601763",
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.ServiceUnavailable, "{}"));

        var originalBackoff = XmPlaylistAlbumResolver.MusicBrainzRetryBackoff;
        XmPlaylistAlbumResolver.MusicBrainzRetryBackoff = TimeSpan.Zero;
        try
        {
            var resolver = new XmPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
            var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

            Assert("not resolved after repeated 503s", resolution.Album == null);
            Assert($"tried three times then gave up (calls: {httpClient.CallCount})", httpClient.CallCount == 4);
        }
        finally
        {
            XmPlaylistAlbumResolver.MusicBrainzRetryBackoff = originalBackoff;
        }
    }

    private static void TestStoreRecordsAndDedupesPlays()
    {
        Console.WriteLine("\n[Test] History store records a play once");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        Assert("first sighting is new", store.TryRecordPlay("p1", "altnation", "Artist One", "Song A", now));
        Assert("same (play, artist) again is deduped", !store.TryRecordPlay("p1", "altnation", "Artist One", "Song A", now));
        Assert("different artist on the same play is a distinct record", store.TryRecordPlay("p1", "altnation", "Artist Two", "Song A", now));
    }

    private static void TestStoreUpsertsTrackAndResolvesToPresentable()
    {
        Console.WriteLine("\n[Test] A resolved track becomes presentable within the window");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        var due = store.GetDueTracks(10);
        Assert("track is due for resolution", due.Count == 1 && due[0].TrackId == "track1");

        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", "artist-mbid-1", "album-mbid-1"), now);

        var presentable = store.GetPresentableTracks("altnation", now - XmPlaylistHistoryStore.PresentationWindow, 10);
        Assert("resolved track is presentable", presentable.Count == 1 && presentable[0].Album == "No Code");
        Assert("artist MBID carried for single-artist track", presentable[0].ArtistMusicBrainzId == "artist-mbid-1");
        Assert("not presentable for another channel", store.GetPresentableTracks("lithium", now - XmPlaylistHistoryStore.PresentationWindow, 10).Count == 0);
    }

    private static void TestStoreThreeStrikesExcludesTrack()
    {
        Console.WriteLine("\n[Test] A track stops being retried after 3 failed attempts");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.RecordTrackFailure("track1");
        store.RecordTrackFailure("track1");
        store.RecordTrackFailure("track1");

        Assert("track excluded from due set after 3 failures", store.GetDueTracks(10).Count == 0);
    }

    private static void TestStorePresentableWindowExpires()
    {
        Console.WriteLine("\n[Test] A resolved track falls out of the presentation window after 25 hours");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, "album-mbid-1"), now - XmPlaylistHistoryStore.PresentationWindow - TimeSpan.FromMinutes(1));

        Assert("old resolution is no longer presentable", store.GetPresentableTracks("altnation", now - XmPlaylistHistoryStore.PresentationWindow, 10).Count == 0);
    }

    private static void TestStorePruneRemovesOldData()
    {
        Console.WriteLine("\n[Test] Prune drops plays and tracks older than the retention window");

        var store = NewHistoryStore();
        var old = DateTime.UtcNow - XmPlaylistHistoryStore.PlayRetention - TimeSpan.FromDays(1);
        var fresh = DateTime.UtcNow;

        store.TryRecordPlay("oldPlay", "altnation", "Old Artist", "Old Song", old);
        store.UpsertTrack("oldTrack", "altnation", new[] { "Old Artist" }, "Old Song", null, null, old);
        store.TryRecordPlay("newPlay", "altnation", "New Artist", "New Song", fresh);
        store.UpsertTrack("newTrack", "altnation", new[] { "New Artist" }, "New Song", null, null, fresh);

        store.Prune();

        Assert("old play pruned", store.GetPlays("altnation", DateTime.MinValue).SingleOrDefault(p => p.Song == "Old Song") == null);
        Assert("fresh play kept", store.GetPlays("altnation", DateTime.MinValue).Any(p => p.Song == "New Song"));
        Assert("old track pruned", store.GetDueTracks(100).All(t => t.TrackId != "oldTrack"));
        Assert("fresh track kept", store.GetDueTracks(100).Any(t => t.TrackId == "newTrack"));
    }

    private static void TestWorkerCapturesDueChannel()
    {
        Console.WriteLine("\n[Test] Worker captures a channel that has never been captured");

        XmPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new XmPlaylistWorker(httpClient, folder, factory, LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new XmPlaylistHistoryStore(folder);
        Assert("play was recorded", store.GetPlays("altnation", DateTime.MinValue).Count == 1);
        Assert("last capture recorded", store.GetLastCaptureUtc("altnation") != null);
        Assert("one feed request made", httpClient.CallCount == 1);
    }

    private static void TestWorkerSkipsCaptureWhenNotDue()
    {
        Console.WriteLine("\n[Test] Worker skips a channel whose capture is not due yet");

        XmPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new XmPlaylistWorker(httpClient, folder, factory, LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);
        var callsAfterFirst = httpClient.CallCount;
        worker.RunOnce(CancellationToken.None);

        Assert($"no additional feed request on the second pass (before {callsAfterFirst}, after {httpClient.CallCount})", httpClient.CallCount == callsAfterFirst);
    }

    private static void TestWorkerResolvesDueTracks()
    {
        Console.WriteLine("\n[Test] Worker resolves captured tracks and they become presentable");

        XmPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "I'm Open", new[] { ("deezer", "https://www.deezer.com/track/624510") })));
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new XmPlaylistWorker(httpClient, folder, factory, LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new XmPlaylistHistoryStore(folder);
        var presentable = store.GetPresentableTracks("altnation", DateTime.UtcNow - XmPlaylistHistoryStore.PresentationWindow, 10);

        Assert("captured + resolved track is presentable", presentable.Count == 1);
        Assert("correct album resolved", presentable[0].Album == "No Code");
        Assert("album MBID attached", presentable[0].AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached (single-artist)", presentable[0].ArtistMusicBrainzId == "artist-mbid-1");
    }

    private static void TestWorkerIdlesWithNoChannels()
    {
        Console.WriteLine("\n[Test] Worker does nothing when no XM Playlist channels are configured");

        XmPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        var factory = new FakeImportListFactory();

        var worker = new XmPlaylistWorker(httpClient, folder, factory, LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        Assert("no HTTP requests made", httpClient.CallCount == 0);
        var store = new XmPlaylistHistoryStore(folder);
        Assert("no plays recorded", store.GetPlays("altnation", DateTime.MinValue).Count == 0);
    }

    private static string BuildFeedJson(params (string PlayId, string TrackId, string Artist, string Title, (string Site, string Url)[] Links)[] plays)
    {
        var entries = new List<string>();
        foreach (var play in plays)
        {
            var linksJson = string.Join(",", play.Links.Select(l => $"{{\"site\":\"{l.Site}\",\"url\":\"{l.Url}\"}}"));
            var linksPart = play.Links.Length == 0 ? "" : $",\"links\":[{linksJson}]";
            entries.Add($"{{\"id\":\"{play.PlayId}\",\"timestamp\":\"2026-08-05T00:00:00Z\"," +
                        $"\"track\":{{\"id\":\"{play.TrackId}\",\"title\":\"{play.Title}\",\"artists\":[\"{play.Artist}\"]}}," +
                        $"\"channelId\":\"altnation\"{linksPart}}}");
        }

        return $"{{\"count\":{entries.Count},\"next\":null,\"previous\":null,\"results\":[{string.Join(",", entries)}]}}";
    }

    private static XmPlaylistHistoryStore NewHistoryStore()
    {
        return new XmPlaylistHistoryStore(NewFolder());
    }

    private static FakeAppFolderInfo NewFolder()
    {
        return new FakeAppFolderInfo(Path.Combine(Path.GetTempPath(), "xmplaylist-test-" + Guid.NewGuid()));
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
    private readonly Dictionary<string, Queue<(HttpStatusCode Status, string Content)>> _sequencesByUrlFragment = new();

    public int CallCount { get; private set; }

    public void Respond(string urlFragment, string jsonContent)
    {
        _responsesByUrlFragment[urlFragment] = jsonContent;
    }

    public void RespondSequence(string urlFragment, params (HttpStatusCode Status, string Content)[] responses)
    {
        var queue = new Queue<(HttpStatusCode, string)>();
        foreach (var response in responses)
        {
            queue.Enqueue(response);
        }

        _sequencesByUrlFragment[urlFragment] = queue;
    }

    public HttpResponse Get(HttpRequest request)
    {
        CallCount++;

        foreach (var pair in _sequencesByUrlFragment)
        {
            if (request.Url.FullUri.Contains(pair.Key, StringComparison.OrdinalIgnoreCase) && pair.Value.Count > 0)
            {
                var (status, content) = pair.Value.Dequeue();
                return new HttpResponse(request, new HttpHeader(), content, status);
            }
        }

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

internal class FakeArtistService : IArtistService
{
    private readonly Dictionary<string, Artist> _byForeignId = new();

    public void Add(Artist artist)
    {
        _byForeignId[artist.ForeignArtistId] = artist;
    }

    public Artist FindById(string foreignArtistId)
    {
        return _byForeignId.TryGetValue(foreignArtistId, out var artist) ? artist : null!;
    }

    public Artist GetArtist(int artistId) => throw new NotSupportedException();
    public Artist GetArtistByMetadataId(int artistMetadataId) => throw new NotSupportedException();
    public List<Artist> GetArtists(IEnumerable<int> artistIds) => throw new NotSupportedException();
    public Artist AddArtist(Artist newArtist, bool doRefresh) => throw new NotSupportedException();
    public List<Artist> AddArtists(List<Artist> newArtists, bool doRefresh) => throw new NotSupportedException();
    public Artist FindByName(string title) => throw new NotSupportedException();
    public Artist FindByNameInexact(string title) => throw new NotSupportedException();
    public List<Artist> GetCandidates(string title) => throw new NotSupportedException();
    public void DeleteArtist(int artistId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public void DeleteArtists(List<int> artistIds, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public List<Artist> GetAllArtists() => throw new NotSupportedException();
    public Dictionary<int, List<int>> GetAllArtistsTags() => throw new NotSupportedException();
    public List<Artist> AllForTag(int tagId) => throw new NotSupportedException();
    public Artist UpdateArtist(Artist artist, bool publishUpdatedEvent = true) => throw new NotSupportedException();
    public List<Artist> UpdateArtists(List<Artist> artist, bool useExistingRelativeFolder) => throw new NotSupportedException();
    public Dictionary<int, string> AllArtistPaths() => throw new NotSupportedException();
    public bool ArtistPathExists(string folder) => throw new NotSupportedException();
    public void RemoveAddOptions(Artist artist) => throw new NotSupportedException();
}

internal class FakeAlbumService : IAlbumService
{
    private readonly Dictionary<string, Album> _byForeignId = new();

    public void Add(Album album)
    {
        _byForeignId[album.ForeignAlbumId] = album;
    }

    public Album FindById(string foreignId)
    {
        return _byForeignId.TryGetValue(foreignId, out var album) ? album : null!;
    }

    public Album GetAlbum(int albumId) => throw new NotSupportedException();
    public List<Album> GetAlbums(IEnumerable<int> albumIds) => throw new NotSupportedException();
    public List<Album> GetAlbumsByArtist(int artistId) => throw new NotSupportedException();
    public List<Album> GetNextAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => throw new NotSupportedException();
    public List<Album> GetLastAlbumsByArtistMetadataId(IEnumerable<int> artistMetadataIds) => throw new NotSupportedException();
    public List<Album> GetAlbumsByArtistMetadataId(int artistMetadataId) => throw new NotSupportedException();
    public List<Album> GetAlbumsForRefresh(int artistMetadataId, List<string> foreignIds) => throw new NotSupportedException();
    public Album AddAlbum(Album newAlbum, bool doRefresh) => throw new NotSupportedException();
    public Album FindByTitle(int artistMetadataId, string title) => throw new NotSupportedException();
    public Album FindByTitleInexact(int artistMetadataId, string title) => throw new NotSupportedException();
    public List<Album> GetCandidates(int artistMetadataId, string title) => throw new NotSupportedException();
    public void DeleteAlbum(int albumId, bool deleteFiles, bool addImportListExclusion = false) => throw new NotSupportedException();
    public List<Album> GetAllAlbums() => throw new NotSupportedException();
    public Album UpdateAlbum(Album album) => throw new NotSupportedException();
    public void SetAlbumMonitored(int albumId, bool monitored) => throw new NotSupportedException();
    public void SetMonitored(IEnumerable<int> ids, bool monitored) => throw new NotSupportedException();
    public void UpdateLastSearchTime(List<Album> albums) => throw new NotSupportedException();
    public PagingSpec<Album> AlbumsWithoutFiles(PagingSpec<Album> pagingSpec) => throw new NotSupportedException();
    public List<Album> AlbumsBetweenDates(DateTime start, DateTime end, bool includeUnmonitored) => throw new NotSupportedException();
    public List<Album> ArtistAlbumsBetweenDates(Artist artist, DateTime start, DateTime end, bool includeUnmonitored) => throw new NotSupportedException();
    public void InsertMany(List<Album> albums) => throw new NotSupportedException();
    public void UpdateMany(List<Album> albums) => throw new NotSupportedException();
    public void DeleteMany(List<Album> albums) => throw new NotSupportedException();
    public void SetAddOptions(IEnumerable<Album> albums) => throw new NotSupportedException();
    public Album FindAlbumByRelease(string albumReleaseId) => throw new NotSupportedException();
    public Album FindAlbumByTrackId(int trackId) => throw new NotSupportedException();
    public List<Album> GetArtistAlbumsWithFiles(Artist artist) => throw new NotSupportedException();
}

internal class FakeCommandQueue : IManageCommandQueue
{
    public List<RefreshArtistCommand> Pushed { get; } = new();

    public CommandModel Push<TCommand>(TCommand command, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified)
        where TCommand : Command
    {
        if (command is RefreshArtistCommand refresh)
        {
            Pushed.Add(refresh);
        }

        return null!;
    }

    public List<CommandModel> PushMany<TCommand>(List<TCommand> commands) where TCommand : Command => throw new NotSupportedException();
    public CommandModel Push(string commandName, DateTime? lastExecutionTime, DateTime? lastStartTime, CommandPriority priority = CommandPriority.Normal, CommandTrigger trigger = CommandTrigger.Unspecified) => throw new NotSupportedException();
    public IEnumerable<CommandModel> Queue(CancellationToken cancellationToken) => throw new NotSupportedException();
    public List<CommandModel> All() => throw new NotSupportedException();
    public CommandModel Get(int id) => throw new NotSupportedException();
    public List<CommandModel> GetStarted() => throw new NotSupportedException();
    public void SetMessage(CommandModel command, string message) => throw new NotSupportedException();
    public void SetResult(CommandModel command, CommandResult result) => throw new NotSupportedException();
    public void Start(CommandModel command) => throw new NotSupportedException();
    public void Complete(CommandModel command, string message) => throw new NotSupportedException();
    public void Fail(CommandModel command, string message, Exception e) => throw new NotSupportedException();
    public void Requeue() => throw new NotSupportedException();
    public void Cancel(int id) => throw new NotSupportedException();
    public void CleanCommands() => throw new NotSupportedException();
}

internal class FakeImportListFactory : IImportListFactory
{
    private readonly List<ImportListDefinition> _definitions = new();

    public void AddChannel(string channel)
    {
        _definitions.Add(new ImportListDefinition
        {
            Implementation = "XmPlaylistImport",
            EnableAutomaticAdd = true,
            Settings = new XmPlaylistImportSettings { Channel = channel }
        });
    }

    public List<ImportListDefinition> All() => _definitions;

    public List<IImportList> AutomaticAddEnabled(bool filterBlockedImportLists = true) => throw new NotSupportedException();
    public List<IImportList> GetAvailableProviders() => throw new NotSupportedException();
    public bool Exists(int id) => throw new NotSupportedException();
    public ImportListDefinition Find(int id) => throw new NotSupportedException();
    public ImportListDefinition Get(int id) => throw new NotSupportedException();
    public IEnumerable<ImportListDefinition> Get(IEnumerable<int> ids) => throw new NotSupportedException();
    public ImportListDefinition Create(ImportListDefinition definition) => throw new NotSupportedException();
    public void Update(ImportListDefinition definition) => throw new NotSupportedException();
    public IEnumerable<ImportListDefinition> Update(IEnumerable<ImportListDefinition> definitions) => throw new NotSupportedException();
    public void Delete(int id) => throw new NotSupportedException();
    public void Delete(IEnumerable<int> ids) => throw new NotSupportedException();
    public IEnumerable<ImportListDefinition> GetDefaultDefinitions() => throw new NotSupportedException();
    public IEnumerable<ImportListDefinition> GetPresetDefinitions(ImportListDefinition providerDefinition) => throw new NotSupportedException();
    public void SetProviderCharacteristics(ImportListDefinition definition) => throw new NotSupportedException();
    public void SetProviderCharacteristics(IImportList provider, ImportListDefinition definition) => throw new NotSupportedException();
    public IImportList GetInstance(ImportListDefinition definition) => throw new NotSupportedException();
    public ValidationResult Test(ImportListDefinition definition) => throw new NotSupportedException();
    public object RequestAction(ImportListDefinition definition, string action, IDictionary<string, string> query) => throw new NotSupportedException();
    public List<ImportListDefinition> AllForTag(int tagId) => throw new NotSupportedException();
}
