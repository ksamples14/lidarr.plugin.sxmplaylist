using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Linq.Expressions;
using System.Threading;
using System.Data.SQLite;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Music.Commands;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex.Server;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using SXMPlaylist.ImportLists;

internal static class Program
{
    private static int _failures;

    public static void Main()
    {
        Console.WriteLine("=== SXMPlaylist Tests ===");

        TestBackfillStopsAtCutoff();
        TestBackfillStopsAtMaxPages();
        TestChannelDirectoryFetchesAndParses();
        TestChannelCacheStoresAndReuses();
        TestSettingsRequireNonEmptyChannel();
        TestSettingsApiPathMirrorsChannelForUiRefresh();
        TestSettingsDefaultReleasePriorityIsSingles();
        TestSettingsDefaultAlbumsPerDayIsMaximum();
        TestImportSplitsAlbumsPerDayAcrossUtcHours();
        TestShowScheduleParsesOfficialEpgShape();
        TestShowScheduleUsesKnownEpgAlias();
        TestShowScheduleSkipsPageFallbackWhenAliasWorks();
        TestShowScheduleDiscoversEpgKeyFromChannelPage();
        TestShowSchedulePrioritizesChannelPageContentId();
        TestShowScheduleTriesMultiplePageCandidateKeys();
        TestShowScheduleParsesEncodedChannelPageKeys();
        TestShowScheduleSkipsPageFallbackWithoutChannelName();
        TestShowScheduleCachesResolvedEpgKey();
        TestStoreFiltersPresentableTracksByShowWindow();
        TestImportAllowsSameChannelDifferentShows();
        TestImportBlocksSecondListWhenDefaultExists();
        TestImportFetchUsesReleasePriorityResolution();
        TestImportSkipsWhenAlternateResolutionIsMonitored();
        TestImportSkipsWhenAlternateResolutionIsOnDisk();
        TestImportDoesNotSkipForUnmonitoredMissingAlternate();
        TestImportBudgetCountsNewAlbumsOnly();
        TestImportBudgetDedupesAlbumsWithinPass();
        TestImportUnlimitedBudgetEmitsAllUncovered();
        TestRefreshSchedulerPushesRefreshForNewlyMonitoredAlbum();

        TestAlbumResolutionViaDeezerAndMusicBrainz();
        TestAlbumResolutionFallsBackToDeezerTitle();
        TestAlbumResolutionFallsBackToAppleMusic();
        TestAlbumResolutionSkipsVariousArtistsCompilations();
        TestAlbumResolutionRejectsDifferentAlbumArtist();
        TestAlbumResolutionPrefersSingleOverEpOverAlbum();
        TestAlbumResolutionCanPreferAlbumOverEpOverSingle();
        TestAlbumResolutionRecordingSearchFindsAlbumAfterIsrcMiss();
        TestAlbumResolutionRecordingSearchRunsWithoutProviderLinks();
        TestAlbumResolutionRecordingSearchAvoidsDuplicateAppleLookup();
        TestAlbumResolutionAppleLookupFailureIsNotRepeated();
        TestAlbumResolutionRecordingSearchCapsDetailLookups();
        TestAlbumResolutionRecordingSearchPrefersStudioRecording();
        TestAlbumResolutionRecordingSearchKeepsSingleCandidate();
        TestAlbumResolutionTitleSearchRecoversAfterIsrcMiss();
        TestAlbumResolutionTitleSearchReturnsBothPrioritiesFromOneLookup();
        TestAlbumResolutionEmptyDeezerResultFallsThroughToApple();
        TestAlbumResolutionTitleSearchRejectsWrongArtist();
        TestAlbumResolutionTitleSearchRejectsOneTokenContainment();
        TestAlbumResolutionTitleSearchViaApple();
        TestAlbumResolutionRanksCompilationLast();
        TestAlbumResolutionRanksMultiTagCompilationLast();
        TestAlbumResolutionAllowsSameArtistCompilationFallback();
        TestAlbumResolutionCompilationFallbackStillExcludesVariousArtists();
        TestAlbumResolutionCompilationFallbackRequiresAllowedStatus();
        TestAlbumResolutionTitleSearchAllowsSameArtistCompilationFallback();
        TestAlbumResolutionTitleSearchStripsEditionSuffixInQuery();
        TestAlbumResolutionRecordingSearchStripsYearSuffix();
        TestAlbumResolutionRecordingSearchUsesDeezerArtistForFragmentedNames();
        TestAlbumResolutionFilterExcludesDisallowedRelease();
        TestMusicBrainzBusyIsRetried();
        TestMusicBrainzGivesUpAfterMaxRetries();

        TestStoreRecordsAndDedupesPlays();
        TestStoreRecordsRepeatedPlayEvents();
        TestStoreMigratesLegacyPlaysToPlayEvents();
        TestStoreAssociatesPlayEventsWithShowWindows();
        TestStorePlayEventsCanBeQueriedByRangeAndShow();
        TestStoreUpsertsTrackAndResolvesToPresentable();
        TestStorePersistsResolutionTraceIdentifiers();
        TestStoreRecordsPlexPlaylistTrackMatches();
        TestStoreReadsLegacyPlexTrackCache();
        TestStoreThreeStrikesExcludesTrack();
        TestStorePresentableWindowExpires();
        TestStoreRequireMbidFiltersBeforeLimit();
        TestStoreMinimumPlaysFiltersBeforeLimit();
        TestStoreLimitCapsPresentationRows();
        TestStorePruneRemovesOldData();
        TestStorePruneRemovesOldPlayEventsAndShowWindows();
        TestStorePruneRemovesOldPlexPlaylistTrackMatches();
        TestStoreHistoryRetentionFiltersQueryOnly();
        TestStoreSchedulesRetryForNoMbidTrack();
        TestStoreRetryGivesUpAfterMaxAttempts();
        TestStoreRetrySuccessClearsRetryState();
        TestStoreNewPlayResetsRetryClock();
        TestStoreRetryFailureRenewsPresentationWindow();
        TestStoreMigrationAddsRetryColumnsIdempotently();

        TestWorkerCapturesDueChannel();
        TestWorkerCapturesPlayEventShowAttribution();
        TestWorkerRecordsPlayEventsWhenEpgFails();
        TestWorkerReusesFreshShowWindowsForDailyEpgRefresh();
        TestWorkerSkipsCaptureWhenNotDue();
        TestWorkerResolvesDueTracks();
        TestWorkerUsesListMetadataProfileForResolution();
        TestWorkerUsesListReleasePriorityForResolution();
        TestWorkerStoresBothReleasePrioritiesForSharedChannel();
        TestWorkerIdlesWithNoChannels();

        TestPlexExactTitleMatch();
        TestPlexSyncReturnsTrackMatchAudit();
        TestPlexFeatCreditStrippedFromArtist();
        TestPlexFuzzyTitleVersionMatch();
        TestPlexRetriesSearchWithStrippedTitleSuffix();
        TestPlexMultiArtistPlayMatchesPrimaryArtist();
        TestPlexRepeatedTrackUsesPersistedCache();
        TestPlexRetriesTransientSearchFailure();
        TestPlexCacheIsolationBetweenLists();
        TestPlexFanOutCreatesPlaylistForFullAccountHomeUser();
        TestPlexFanOutCachesPlexTvUserDiscovery();
        TestPlexFanOutSkipsPinProtectedUser();
        TestPlexFanOutUsesSwitchFallbackWhenNoSharedToken();
        TestPlexFanOutOwnerSyncSurvivesHomeUsersFailure();
        TestPlexFanOutCoversSharedUsersOutsideHome();
        TestPlexFanOutSkipsUsersWithoutMusicShare();
        TestPlexCleanupDeletesOwnerAndUserCopies();
        TestPlexCleanupDefersWhenPlexTvUnreachable();
        TestPlexPruneRemovesUnsharedUserCopies();
        TestPlexPruneKeepsAllWhenPlexTvUnreachable();

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
        var response = SXMPlaylistStationBackfill.Fetch(request, TimeSpan.FromHours(6), fetchPage);

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
        SXMPlaylistStationBackfill.Fetch(request, TimeSpan.FromDays(30), fetchPage);

        Assert($"never exceeds MaxPages (fetched {fetchCount}, cap {SXMPlaylistStationBackfill.MaxPages})", fetchCount == SXMPlaylistStationBackfill.MaxPages);
    }

    private static void TestChannelDirectoryFetchesAndParses()
    {
        Console.WriteLine("\n[Test] Channel directory parses /api/station into deeplink/name/number");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station", "{\"count\":2,\"results\":[" +
            "{\"deeplink\":\"altnation\",\"name\":\"Alt Nation\",\"number\":\"36\"}," +
            "{\"deeplink\":\"thespectrum\",\"name\":\"The Spectrum\",\"number\":\"28\"}]}");

        var channels = SXMPlaylistChannelDirectory.Fetch(httpClient, "https://xmplaylist.com");

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

        var none = new SXMPlaylistImportSettings { Channel = "" };
        var one = new SXMPlaylistImportSettings { Channel = "altnation" };

        Assert("empty channel fails validation", !none.Validate().IsValid);
        Assert("a selected channel passes validation", one.Validate().IsValid);
    }

    private static void TestSettingsApiPathMirrorsChannelForUiRefresh()
    {
        Console.WriteLine("\n[Test] Settings apiPath mirrors Channel for Lidarr dynamic option refresh");

        var settings = new SXMPlaylistImportSettings { Channel = "altnation" };
        Assert("apiPath getter exposes the selected channel", settings.ApiPath == "altnation");

        settings.ApiPath = "alt2k";
        Assert("apiPath setter updates backend Channel", settings.Channel == "alt2k");
    }

    private static void TestSettingsDefaultReleasePriorityIsSingles()
    {
        Console.WriteLine("\n[Test] Settings default release priority keeps singles-first behavior");

        var settings = new SXMPlaylistImportSettings();

        Assert("default release priority is Singles", settings.ReleasePriority == ReleasePriorityMode.Singles);
    }

    private static void TestSettingsDefaultAlbumsPerDayIsMaximum()
    {
        Console.WriteLine("\n[Test] Settings default albums per day is 24, zero is unlimited");

        var settings = new SXMPlaylistImportSettings();
        var unlimited = new SXMPlaylistImportSettings { Channel = "altnation", AlbumsPerDay = 0 };
        var tooMany = new SXMPlaylistImportSettings { Channel = "altnation", AlbumsPerDay = 501 };

        Assert("default albums per day is 24", settings.AlbumsPerDay == 24);
        Assert("albums per day accepts zero as unlimited", unlimited.Validate().IsValid);
        Assert("albums per day rejects values above 500", !tooMany.Validate().IsValid);
    }

    private static void TestImportSplitsAlbumsPerDayAcrossUtcHours()
    {
        Console.WriteLine("\n[Test] Import splits albums per day across UTC hours");

        var max = new SXMPlaylistImportSettings { AlbumsPerDay = 500 };
        var unlimited = new SXMPlaylistImportSettings { AlbumsPerDay = 0 };
        var one = new SXMPlaylistImportSettings { AlbumsPerDay = 1 };
        var fortyEight = new SXMPlaylistImportSettings { AlbumsPerDay = 48 };

        var maxHourly = Enumerable.Range(0, 24)
            .Select(h => SXMPlaylistImport.GetAlbumsPerFetch(max, new DateTime(2026, 8, 10, h, 0, 0, DateTimeKind.Utc)))
            .ToList();
        var oneHourly = Enumerable.Range(0, 24)
            .Select(h => SXMPlaylistImport.GetAlbumsPerFetch(one, new DateTime(2026, 8, 10, h, 0, 0, DateTimeKind.Utc)))
            .ToList();

        Assert("500/day totals 500 across 24 hourly windows", maxHourly.Sum() == 500);
        Assert("500/day spreads as 20 or 21 per hour", maxHourly.Min() == 20 && maxHourly.Max() == 21);
        Assert("0/day is unlimited", SXMPlaylistImport.GetAlbumsPerFetch(unlimited, new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc)) == int.MaxValue);
        Assert("1/day exposes only one hourly slot", oneHourly.Sum() == 1 && oneHourly.Count(v => v == 1) == 1);
        Assert("48/day is exactly 2 per hour", SXMPlaylistImport.GetAlbumsPerFetch(fortyEight, new DateTime(2026, 8, 10, 23, 0, 0, DateTimeKind.Utc)) == 2);
    }

    private static void TestShowScheduleParsesOfficialEpgShape()
    {
        Console.WriteLine("\n[Test] Show schedule parses SiriusXM EPG episode windows");

        var json = "{\"chEpgInfo\":{\"dayChSchedules\":[{\"episode\":[" +
                   "{\"pgid\":16824,\"pr\":{\"pName\":\"The Alt-18- Most Requested Countdown!\"},\"sc\":{\"sTimeStr\":\"08.08.2026 18:00 EDT\",\"eTimeStr\":\"08.08.2026 19:00 EDT\"}}," +
                   "{\"pgid\":16606,\"pr\":{\"pName\":\"Alt Nation\"},\"sc\":{\"sTimeStr\":\"08.08.2026 19:00 EDT\",\"eTimeStr\":\"08.08.2026 20:00 EDT\"}}" +
                   "]}],\"pg\":[]}}";

        var shows = SXMPlaylistShowSchedule.Parse(json);
        var alt18 = shows.SingleOrDefault(s => s.ProgramId == "16824");

        Assert($"parses both scheduled shows (got {shows.Count})", shows.Count == 2);
        Assert("uses program name from episode pr.pName", alt18?.Name == "The Alt-18- Most Requested Countdown!");
        Assert("parses Eastern show window to UTC", alt18?.Windows.Count == 1 && alt18.Windows[0].Contains(new DateTime(2026, 8, 8, 22, 30, 0, DateTimeKind.Utc)));
    }

    private static void TestShowScheduleUsesKnownEpgAlias()
    {
        Console.WriteLine("\n[Test] Show schedule uses known SiriusXM EPG key aliases");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=theroadhouse", BuildEpgJson("17436", "Willie's Roadhouse with Dallas Wayne"));

        var shows = SXMPlaylistShowSchedule.Fetch(httpClient, "williesroadhouse", "Willie's Roadhouse");

        Assert("Willie's Roadhouse resolves through theroadhouse EPG key", shows.Any(s => s.ProgramId == "17436"));
        Assert("known alias is tried before the xmplaylist deeplink", httpClient.LastRequestUrl.Contains("channelKeys=theroadhouse"));
    }

    private static void TestShowScheduleSkipsPageFallbackWhenAliasWorks()
    {
        Console.WriteLine("\n[Test] Show schedule does not fetch channel page when an alias works");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=theroadhouse", BuildEpgJson("17436", "Willie's Roadhouse with Dallas Wayne"));
        httpClient.Respond("/channels/willie-s-roadhouse", "{\"channelId\":\"wrongkey\"}");

        SXMPlaylistShowSchedule.Fetch(httpClient, "williesroadhouse", "Willie's Roadhouse");

        Assert("alias success only made one request", httpClient.CallCount == 1);
        Assert("channel page was not fetched", !httpClient.RequestUrls.Any(u => u.Contains("/channels/")));
    }

    private static void TestShowScheduleDiscoversEpgKeyFromChannelPage()
    {
        Console.WriteLine("\n[Test] Show schedule discovers SiriusXM EPG key from channel page");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=xmplaylistkey", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        httpClient.Respond("/channels/example-channel", "{\"shows\":[{\"name\":\"Example Show\",\"channelId\":\"realkey\"}]}");
        httpClient.Respond("channelKeys=realkey", BuildEpgJson("999", "Example Show"));

        var shows = SXMPlaylistShowSchedule.Fetch(httpClient, "xmplaylistkey", "Example Channel");

        Assert("page channelId is used as an EPG fallback", shows.Count == 1 && shows[0].ProgramId == "999");
        Assert("fallback requests the discovered EPG key", httpClient.LastRequestUrl.Contains("channelKeys=realkey"));
    }

    private static void TestShowSchedulePrioritizesChannelPageContentId()
    {
        Console.WriteLine("\n[Test] Show schedule prioritizes channel page content id over related show channel ids");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=thebeatleschannel", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        httpClient.Respond("/channels/the-beatles-channel",
            "{\"channelId\":\"classicvinyl\"}" +
            "<meta class=\"swiftype\" name=\"contentid\" data-type=\"enum\" content=\"9446\"/>" +
            "{\"channel_id\":\"9446\",\"siriusChannelNumber\":18}");
        httpClient.Respond("channelKeys=classicvinyl", BuildEpgJson("111", "Classic Vinyl"));
        httpClient.Respond("channelKeys=9446", BuildEpgJson("15839", "Breakfast With The Beatles"));

        var shows = SXMPlaylistShowSchedule.Fetch(httpClient, "thebeatleschannel", "The Beatles Channel");

        Assert("Beatles content id selected before related channel ids", shows.Count == 1 && shows[0].ProgramId == "15839");
        Assert("related channel id was not requested after content id resolved", !httpClient.RequestUrls.Any(u => u.Contains("channelKeys=classicvinyl")));
    }

    private static void TestShowScheduleTriesMultiplePageCandidateKeys()
    {
        Console.WriteLine("\n[Test] Show schedule tries multiple channel page EPG key candidates");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=multi-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        httpClient.Respond("/channels/multiple-candidates", "{\"channelId\":\"bad-key\"},{\"channelId\":\"good_key\"}");
        httpClient.Respond("channelKeys=bad-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        httpClient.Respond("channelKeys=good_key", BuildEpgJson("1000", "Good Candidate"));

        var shows = SXMPlaylistShowSchedule.Fetch(httpClient, "multi-key", "Multiple Candidates");

        Assert("second page candidate resolved", shows.Count == 1 && shows[0].ProgramId == "1000");
        Assert("hyphen and underscore candidate keys were requested", httpClient.RequestUrls.Any(u => u.Contains("channelKeys=bad-key")) && httpClient.RequestUrls.Any(u => u.Contains("channelKeys=good_key")));
    }

    private static void TestShowScheduleParsesEncodedChannelPageKeys()
    {
        Console.WriteLine("\n[Test] Show schedule parses encoded channel page EPG keys");

        var htmlEncoded = new FakeHttpClient();
        htmlEncoded.Respond("channelKeys=html-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        htmlEncoded.Respond("/channels/html-encoded", "{&quot;channelId&quot;:&quot;htmlreal&quot;}");
        htmlEncoded.Respond("channelKeys=htmlreal", BuildEpgJson("1001", "HTML Candidate"));

        var escaped = new FakeHttpClient();
        escaped.Respond("channelKeys=escaped-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        escaped.Respond("/channels/escaped-encoded", "{\\\"channelId\\\":\\\"escapedreal\\\"}");
        escaped.Respond("channelKeys=escapedreal", BuildEpgJson("1002", "Escaped Candidate"));

        Assert("HTML-encoded page key resolved", SXMPlaylistShowSchedule.Fetch(htmlEncoded, "html-key", "HTML Encoded").Any(s => s.ProgramId == "1001"));
        Assert("backslash-escaped page key resolved", SXMPlaylistShowSchedule.Fetch(escaped, "escaped-key", "Escaped Encoded").Any(s => s.ProgramId == "1002"));
    }

    private static void TestShowScheduleSkipsPageFallbackWithoutChannelName()
    {
        Console.WriteLine("\n[Test] Show schedule skips channel page fallback without a channel name");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("channelKeys=no-name-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");

        var shows = SXMPlaylistShowSchedule.Fetch(httpClient, "no-name-key");

        Assert("no channel name returns no shows when direct EPG is empty", shows.Count == 0);
        Assert("no channel page request without channel name", httpClient.CallCount == 1 && !httpClient.RequestUrls.Any(u => u.Contains("/channels/")));
    }

    private static void TestShowScheduleCachesResolvedEpgKey()
    {
        Console.WriteLine("\n[Test] Show schedule caches resolved EPG keys");

        var first = new FakeHttpClient();
        first.Respond("channelKeys=cache-key", "{\"chEpgInfo\":{\"dayChSchedules\":[],\"pg\":[]}}");
        first.Respond("/channels/cache-channel", "{\"channelId\":\"cachedreal\"}");
        first.Respond("channelKeys=cachedreal", BuildEpgJson("1003", "Cached Candidate"));

        var second = new FakeHttpClient();
        second.Respond("channelKeys=cachedreal", BuildEpgJson("1003", "Cached Candidate"));

        SXMPlaylistShowSchedule.Fetch(first, "cache-key", "Cache Channel");
        var shows = SXMPlaylistShowSchedule.Fetch(second, "cache-key", "Cache Channel");

        Assert("second call reused cached key", shows.Any(s => s.ProgramId == "1003"));
        Assert("cached call skipped direct miss and channel page", second.CallCount == 1 && second.LastRequestUrl.Contains("channelKeys=cachedreal"));
    }

    private static void TestStoreFiltersPresentableTracksByShowWindow()
    {
        Console.WriteLine("\n[Test] Presentable tracks can be filtered by show windows before limit");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;
        var showTime = now.AddHours(-2);
        var otherTime = now.AddHours(-1);

        for (var i = 0; i < 25; i++)
        {
            var trackId = "other" + i;
            store.UpsertTrack(trackId, "altnation", new[] { "Other Artist" }, "Other Song", null, null, otherTime.AddMinutes(i));
            store.MarkTrackResolved(trackId, new AlbumResolution(true, "Other Album", null, "other-album" + i), now.AddMinutes(i));
        }

        store.UpsertTrack("showTrack", "altnation", new[] { "Show Artist" }, "Show Song", null, null, showTime);
        store.MarkTrackResolved("showTrack", new AlbumResolution(true, "Show Album", null, "show-album"), now.AddMinutes(-30));

        var windows = new[] { new ShowWindow(showTime.AddMinutes(-5), showTime.AddMinutes(5)) };
        var presentable = store.GetPresentableTracks("altnation", now - SXMPlaylistHistoryStore.PresentationWindow, 20, windows);

        Assert("show-window match is returned even when newer non-show rows exceed limit", presentable.Count == 1 && presentable[0].TrackId == "showTrack");
        Assert("empty show windows return no tracks", store.GetPresentableTracks("altnation", now - SXMPlaylistHistoryStore.PresentationWindow, 20, Array.Empty<ShowWindow>()).Count == 0);
    }

    private static void TestImportAllowsSameChannelDifferentShows()
    {
        Console.WriteLine("\n[Test] Import UI allows same channel with different unused shows");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        store.SaveChannels(new[] { new ChannelInfo("altnation", "Alt Nation", "36") });

        var httpClient = new FakeHttpClient();
        httpClient.Respond("sxmepg/epg.sxmchepginfo.xmc", BuildEpgJson());

        var repo = new FakeImportListRepository();
        repo.Add(1, "altnation", "16824");
        var import = NewImport(httpClient, folder, repo, 0, "altnation", SXMPlaylistShowSchedule.ChannelValue);

        var channels = GetOptionValues(import.RequestAction("getChannels", new Dictionary<string, string>()));
        var shows = GetOptionValues(import.RequestAction("getShows", new Dictionary<string, string> { ["channel"] = "altnation" }));

        Assert("channel remains selectable when only a show-specific list exists", channels.Contains("altnation"));
        Assert("already-used show is removed", !shows.Contains("16824"));
        Assert("another show remains selectable", shows.Contains("16606"));
        Assert("default Channel option remains selectable when no default list exists", shows.Contains(SXMPlaylistShowSchedule.ChannelValue));
    }

    private static void TestImportBlocksSecondListWhenDefaultExists()
    {
        Console.WriteLine("\n[Test] Import UI blocks a second list when the channel default exists");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        store.SaveChannels(new[] { new ChannelInfo("altnation", "Alt Nation", "36") });

        var repo = new FakeImportListRepository();
        repo.Add(1, "altnation", SXMPlaylistShowSchedule.ChannelValue);
        var import = NewImport(new FakeHttpClient(), folder, repo, 0, "", SXMPlaylistShowSchedule.ChannelValue);

        var channels = GetOptionValues(import.RequestAction("getChannels", new Dictionary<string, string>()));

        Assert("channel with an existing default list is hidden for new lists", !channels.Contains("altnation"));
    }

    private static void TestImportFetchUsesReleasePriorityResolution()
    {
        Console.WriteLine("\n[Test] Import fetch reads the resolution slot selected by list release priority");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", ReleasePriorityMode.Singles, new AlbumResolution(true, "The Single", "artist-mbid-1", "single-mbid"), now);
        store.MarkTrackResolved("track1", ReleasePriorityMode.Albums, new AlbumResolution(true, "The Album", "artist-mbid-1", "album-mbid"), now);

        var repo = new FakeImportListRepository();
        var singlesImport = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Singles);
        var albumsImport = NewImport(new FakeHttpClient(), folder, repo, 2, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums);

        var singles = singlesImport.Fetch();
        var albums = albumsImport.Fetch();

        Assert("singles list fetch emits the single", singles.Count == 1 && singles[0].Album == "The Single" && singles[0].AlbumMusicBrainzId == "single-mbid");
        Assert("albums list fetch emits the album", albums.Count == 1 && albums[0].Album == "The Album" && albums[0].AlbumMusicBrainzId == "album-mbid");
    }

    private static void TestImportSkipsWhenAlternateResolutionIsMonitored()
    {
        Console.WriteLine("\n[Test] Import skips preferred release when alternate resolution is monitored");

        var folder = NewFolder();
        SeedDualResolutionTrack(folder);
        var albumService = new FakeAlbumService();
        albumService.Add(new Album { ForeignAlbumId = "album-mbid", Monitored = true });

        var repo = new FakeImportListRepository();
        var singlesImport = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Singles, albumService);
        var albumsImport = NewImport(new FakeHttpClient(), folder, repo, 2, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums, albumService);

        Assert("singles preferred skipped because album is monitored", singlesImport.Fetch().Count == 0);
        Assert("albums preferred skipped because preferred album is monitored", albumsImport.Fetch().Count == 0);
    }

    private static void TestImportSkipsWhenAlternateResolutionIsOnDisk()
    {
        Console.WriteLine("\n[Test] Import skips preferred release when alternate resolution is on disk");

        var folder = NewFolder();
        SeedDualResolutionTrack(folder);
        var albumService = new FakeAlbumService();
        var artist = new Artist { Id = 42 };
        albumService.Add(new Album { Id = 10, ForeignAlbumId = "single-mbid", Monitored = false, Artist = artist });
        albumService.AddWithFiles(new Album { Id = 10, ForeignAlbumId = "single-mbid" }, artist);

        var repo = new FakeImportListRepository();
        var albumsImport = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums, albumService);

        Assert("albums preferred skipped because single is on disk", albumsImport.Fetch().Count == 0);
    }

    private static void TestImportDoesNotSkipForUnmonitoredMissingAlternate()
    {
        Console.WriteLine("\n[Test] Import does not skip when alternate resolution only exists as unmonitored metadata");

        var folder = NewFolder();
        SeedDualResolutionTrack(folder);
        var albumService = new FakeAlbumService();
        albumService.Add(new Album { ForeignAlbumId = "album-mbid", Monitored = false });

        var repo = new FakeImportListRepository();
        var singlesImport = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Singles, albumService);
        var singles = singlesImport.Fetch();

        Assert("singles preferred still emits single when album is unmonitored and missing", singles.Count == 1 && singles[0].AlbumMusicBrainzId == "single-mbid");
    }

    private static void TestImportBudgetCountsNewAlbumsOnly()
    {
        Console.WriteLine("\n[Test] Import budget counts new albums only, not covered ones");

        // AlbumsPerDay = 24 -> hourly budget of 1 regardless of the current UTC hour. Three
        // presentable tracks resolve to three albums; the two newest are already covered, the
        // oldest is new. The budget must still emit the new album rather than returning nothing.
        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        store.UpsertTrack("track-oldest", "altnation", new[] { "Artist One" }, "Song A", null, null, now.AddMinutes(-3));
        store.MarkTrackResolved("track-oldest", ReleasePriorityMode.Albums, new AlbumResolution(true, "New Album", "artist-mbid-1", "new-mbid"), now.AddMinutes(-3));
        store.UpsertTrack("track-new2", "altnation", new[] { "Artist Two" }, "Song B", null, null, now.AddMinutes(-2));
        store.MarkTrackResolved("track-new2", ReleasePriorityMode.Albums, new AlbumResolution(true, "Covered Album 2", "artist-mbid-2", "covered2-mbid"), now.AddMinutes(-2));
        store.UpsertTrack("track-new1", "altnation", new[] { "Artist Three" }, "Song C", null, null, now.AddMinutes(-1));
        store.MarkTrackResolved("track-new1", ReleasePriorityMode.Albums, new AlbumResolution(true, "Covered Album 1", "artist-mbid-3", "covered1-mbid"), now.AddMinutes(-1));

        var albumService = new FakeAlbumService();
        albumService.Add(new Album { ForeignAlbumId = "covered1-mbid", Monitored = true });
        albumService.Add(new Album { ForeignAlbumId = "covered2-mbid", Monitored = true });

        var repo = new FakeImportListRepository();
        var import = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums, albumService);
        ((SXMPlaylistImportSettings)import.Definition.Settings).AlbumsPerDay = 24;

        var items = import.Fetch();

        Assert("budget of 1 emits the one new album despite two newer covered ones", items.Count == 1 && items[0].AlbumMusicBrainzId == "new-mbid");
    }

    private static void TestImportBudgetDedupesAlbumsWithinPass()
    {
        Console.WriteLine("\n[Test] Import budget counts a repeated album once within a pass");

        // AlbumsPerDay = 48 -> hourly budget of 2. Three presentable tracks all resolve to the SAME
        // new album. The budget must emit the album once, not three times.
        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 3; i++)
        {
            store.UpsertTrack($"track{i}", "altnation", new[] { "Artist One" }, $"Song {i}", null, null, now.AddMinutes(-i));
            store.MarkTrackResolved($"track{i}", ReleasePriorityMode.Albums, new AlbumResolution(true, "The Album", "artist-mbid-1", "album-mbid"), now.AddMinutes(-i));
        }

        var repo = new FakeImportListRepository();
        var import = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums);
        ((SXMPlaylistImportSettings)import.Definition.Settings).AlbumsPerDay = 48;

        var items = import.Fetch();

        Assert("repeated album counted once within the pass", items.Count == 1 && items[0].AlbumMusicBrainzId == "album-mbid");
    }

    private static void TestImportUnlimitedBudgetEmitsAllUncovered()
    {
        Console.WriteLine("\n[Test] Import unlimited budget emits all uncovered albums");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", ReleasePriorityMode.Albums, new AlbumResolution(true, "New Album", "artist-mbid-1", "new-mbid"), now);
        store.UpsertTrack("track2", "altnation", new[] { "Artist Two" }, "Song B", null, null, now.AddMinutes(-1));
        store.MarkTrackResolved("track2", ReleasePriorityMode.Albums, new AlbumResolution(true, "Another Album", "artist-mbid-2", "another-mbid"), now.AddMinutes(-1));
        store.UpsertTrack("track3", "altnation", new[] { "Artist Three" }, "Song C", null, null, now.AddMinutes(-2));
        store.MarkTrackResolved("track3", ReleasePriorityMode.Albums, new AlbumResolution(true, "Covered Album", "artist-mbid-3", "covered-mbid"), now.AddMinutes(-2));

        var albumService = new FakeAlbumService();
        albumService.Add(new Album { ForeignAlbumId = "covered-mbid", Monitored = true });

        var repo = new FakeImportListRepository();
        var import = NewImport(new FakeHttpClient(), folder, repo, 1, "altnation", SXMPlaylistShowSchedule.ChannelValue, ReleasePriorityMode.Albums, albumService);
        ((SXMPlaylistImportSettings)import.Definition.Settings).AlbumsPerDay = 0;

        var items = import.Fetch();

        Assert("unlimited emits both uncovered albums and skips the covered one", items.Count == 2 && !items.Any(i => i.AlbumMusicBrainzId == "covered-mbid"));
    }

    private static void SeedDualResolutionTrack(FakeAppFolderInfo folder)
    {
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", ReleasePriorityMode.Singles, new AlbumResolution(true, "The Single", "artist-mbid-1", "single-mbid"), now);
        store.MarkTrackResolved("track1", ReleasePriorityMode.Albums, new AlbumResolution(true, "The Album", "artist-mbid-1", "album-mbid"), now);
    }

    private static SXMPlaylistImport NewImport(FakeHttpClient httpClient, FakeAppFolderInfo folder, FakeImportListRepository repo, int id, string channel, string show, ReleasePriorityMode releasePriority = ReleasePriorityMode.Singles, FakeAlbumService? albumService = null)
    {
        albumService ??= new FakeAlbumService();
        var import = new SXMPlaylistImport(
            httpClient,
            null!,
            null!,
            null!,
            folder,
            new FakeArtistService(),
            albumService,
            new FakeCommandQueue(),
            repo,
            LogManager.GetLogger("Test"));

        import.Definition = new ImportListDefinition
        {
            Id = id,
            Implementation = nameof(SXMPlaylistImport),
            Settings = new SXMPlaylistImportSettings { Channel = channel, Show = show, ReleasePriority = releasePriority }
        };

        return import;
    }

    private static HashSet<string> GetOptionValues(object response)
    {
        var options = (System.Collections.IEnumerable)response.GetType().GetProperty("options")!.GetValue(response)!;
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            values.Add((string)option.GetType().GetProperty("Value")!.GetValue(option)!);
        }

        return values;
    }

    private static string BuildEpgJson()
    {
        return "{\"chEpgInfo\":{\"dayChSchedules\":[{\"episode\":[" +
               "{\"pgid\":16824,\"pr\":{\"pName\":\"The Alt-18- Most Requested Countdown!\"},\"sc\":{\"sTimeStr\":\"08.08.2026 18:00 EDT\",\"eTimeStr\":\"08.08.2026 19:00 EDT\"}}," +
               "{\"pgid\":16606,\"pr\":{\"pName\":\"Alt Nation\"},\"sc\":{\"sTimeStr\":\"08.08.2026 19:00 EDT\",\"eTimeStr\":\"08.08.2026 20:00 EDT\"}}" +
               "]}],\"pg\":[]}}";
    }

    private static string BuildEpgJson(string programId, string showName)
    {
        return "{\"chEpgInfo\":{\"dayChSchedules\":[{\"episode\":[" +
               $"{{\"pgid\":{programId},\"pr\":{{\"pName\":\"{showName}\"}},\"sc\":{{\"sTimeStr\":\"08.08.2026 18:00 EDT\",\"eTimeStr\":\"08.08.2026 19:00 EDT\"}}}}" +
               "]}],\"pg\":[]}}";
    }

    private static void TestRefreshSchedulerPushesRefreshForNewlyMonitoredAlbum()
    {
        Console.WriteLine("\n[Test] Refresh scheduler pushes RefreshArtistCommand only for an existing artist's newly-monitored album");

        var artists = new FakeArtistService();
        var albums = new FakeAlbumService();
        var queue = new FakeCommandQueue();
        var scheduler = new SXMPlaylistRefreshScheduler(artists, albums, queue, LogManager.GetLogger("Test"));

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

        // Artist refreshed within the last day -> throttled, no refresh (full discography pulls are heavy).
        artists.Add(new Artist { Id = 9, Name = "Incubus", ForeignArtistId = "artist-mbid-9", LastInfoSync = DateTime.UtcNow });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-9", Monitored = false });
        scheduler.Schedule(new List<ImportListItemInfo> { item("artist-mbid-9", "album-mbid-9") });
        Assert($"no refresh for an artist refreshed within the last day (pushed {queue.Pushed.Count})", queue.Pushed.Count == 1);

        // Artist last refreshed more than a day ago -> refresh fires.
        artists.Add(new Artist { Id = 10, Name = "Nirvana", ForeignArtistId = "artist-mbid-10", LastInfoSync = DateTime.UtcNow.AddDays(-2) });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-10", Monitored = false });
        scheduler.Schedule(new List<ImportListItemInfo> { item("artist-mbid-10", "album-mbid-10") });
        Assert($"refresh fires for an artist not refreshed in a day (pushed {queue.Pushed.Count})", queue.Pushed.Count == 2);
        Assert("targets the stale artist", queue.Pushed[1].ArtistIds.SequenceEqual(new List<int> { 10 }));

        artists.Add(new Artist { Id = 5, Name = "Phoenix", ForeignArtistId = "artist-mbid-5" });
        albums.Add(new Album { ForeignAlbumId = "album-mbid-5", Monitored = false });
        scheduler.Schedule(new List<ImportListItemInfo>
        {
            item("artist-mbid-1", "album-mbid-1"),
            item("artist-mbid-5", "album-mbid-5")
        });
        Assert($"second batch pushed one more command (pushed {queue.Pushed.Count})", queue.Pushed.Count == 3);
        Assert("batches distinct artist ids", queue.Pushed[2].ArtistIds.SequenceEqual(new List<int> { 1, 5 }));

        scheduler.Schedule(null);
        Assert($"no command for an empty batch (pushed {queue.Pushed.Count})", queue.Pushed.Count == 3);
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

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("resolved to a real title", resolution.Album == "No Code");
        Assert("album MBID attached", resolution.AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached", resolution.ArtistMusicBrainzId == "artist-mbid-1");
        Assert("recording MBID attached", resolution.RecordingMusicBrainzId == "rec-1");
        Assert("ISRC attached", resolution.Isrc == "USSM19601763");
        Assert("resolution method records ISRC path", resolution.ResolutionMethod == "isrc");
        Assert("Deezer, ISRC, and recording endpoints were each called once", httpClient.CallCount == 3);
    }

    private static void TestAlbumResolutionFallsBackToDeezerTitle()
    {
        Console.WriteLine("\n[Test] When MusicBrainz can't resolve an ISRC match, Deezer's own album title is used");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"album\":{\"title\":\"No Code\"}}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("falls back to Deezer's own album title", resolution.Album == "No Code");
        Assert("no album MBID (no MusicBrainz match)", resolution.AlbumMusicBrainzId == null);
        Assert("Deezer call plus one MB recording-search attempt (calls: " + httpClient.CallCount + ")", httpClient.CallCount == 3);
    }

    private static void TestAlbumResolutionFallsBackToAppleMusic()
    {
        Console.WriteLine("\n[Test] Falls back to Apple Music lookup when there's no Deezer link");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"No Code\"}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("resolved via Apple fallback", resolution.Album == "No Code");
        Assert("no album MBID (Apple path doesn't have one)", resolution.AlbumMusicBrainzId == null);
    }

    private static void TestAlbumResolutionSkipsVariousArtistsCompilations()
    {
        Console.WriteLine("\n[Test] Album resolution skips Various Artists compilations and prefers the artist's own release");

        // Case 1: a VA compilation (Album type) plus the artist's own single -> the artist's own wins.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"va-mbid\",\"name\":\"Various Artists\"}}]," +
            "\"release-group\":{\"id\":\"comp-mbid\",\"title\":\"Summer Hits 2026\",\"primary-type\":\"Album\",\"first-release-date\":\"2026-01-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("artist's own single preferred over VA compilation", resolution.Album == "No Code");
        Assert("compilation MBID not used", resolution.AlbumMusicBrainzId == "album-mbid-1");

        // Case 2: only VA releases exist -> MusicBrainz path falls through to Deezer's own title.
        var httpClient2 = new FakeHttpClient();
        httpClient2.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"No Code\"}}");
        httpClient2.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-2\"}]}");
        httpClient2.Respond("musicbrainz.org/ws/2/recording/rec-2",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"va-mbid\",\"name\":\"Various Artists\"}}]," +
            "\"release-group\":{\"id\":\"comp-mbid\",\"title\":\"Summer Hits 2026\",\"primary-type\":\"Album\",\"first-release-date\":\"2026-01-01\"}}]}");
        var resolver2 = new SXMPlaylistAlbumResolver(httpClient2, LogManager.GetLogger("Test"));
        var resolution2 = resolver2.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("falls back to Deezer title when only VA releases exist", resolution2.Album == "No Code");
        Assert("no compilation MBID used", resolution2.AlbumMusicBrainzId == null);
    }

    private static void TestAlbumResolutionRejectsDifferentAlbumArtist()
    {
        Console.WriteLine("\n[Test] Exact MusicBrainz matches reject release-groups credited to a different artist");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"The Five Pennies\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"louis-armstrong-mbid\",\"name\":\"Louis Armstrong\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"danny-kaye-mbid\",\"name\":\"Danny Kaye\"}}]," +
            "\"release-group\":{\"id\":\"soundtrack-mbid\",\"title\":\"The Five Pennies\",\"primary-type\":\"Album\",\"secondary-types\":[\"Soundtrack\"],\"first-release-date\":\"1959-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Louis Armstrong", "After You've Gone", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("different-artist soundtrack MBID not attached", resolution.AlbumMusicBrainzId == null);
        Assert("different album artist MBID not attached", resolution.ArtistMusicBrainzId == null);
        Assert("falls back to Deezer title", resolution.Album == "The Five Pennies");
    }

    private static void TestAlbumResolutionPrefersSingleOverEpOverAlbum()
    {
        Console.WriteLine("\n[Test] Album resolution prefers Single over EP over Album for the artist's own releases");

        // Single + EP + Album by the same artist -> the Single wins.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"ep-mbid\",\"title\":\"The EP\",\"primary-type\":\"EP\",\"first-release-date\":\"1996-07-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("single preferred over EP and album", resolution.Album == "The Single");
        Assert("single MBID used", resolution.AlbumMusicBrainzId == "single-mbid");

        // EP + Album only (no single) -> the EP wins.
        var httpClient2 = new FakeHttpClient();
        httpClient2.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient2.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-2\"}]}");
        httpClient2.Respond("musicbrainz.org/ws/2/recording/rec-2",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"ep-mbid\",\"title\":\"The EP\",\"primary-type\":\"EP\",\"first-release-date\":\"1996-07-01\"}}]}");
        var resolver2 = new SXMPlaylistAlbumResolver(httpClient2, LogManager.GetLogger("Test"));
        var resolution2 = resolver2.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("EP preferred over album when no single exists", resolution2.Album == "The EP");
    }

    private static void TestAlbumResolutionCanPreferAlbumOverEpOverSingle()
    {
        Console.WriteLine("\n[Test] Album resolution can prefer Album over EP over Single when configured");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"ep-mbid\",\"title\":\"The EP\",\"primary-type\":\"EP\",\"first-release-date\":\"1996-07-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var albumsFirst = new AlbumTypeFilter(
            new HashSet<string> { "Single", "EP", "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" },
            ReleasePriorityMode.Albums);

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")), albumsFirst);

        Assert("album preferred over EP and single", resolution.Album == "The Album");
        Assert("album MBID used", resolution.AlbumMusicBrainzId == "album-mbid");

        var httpClient2 = new FakeHttpClient();
        httpClient2.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient2.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-2\"}]}");
        httpClient2.Respond("musicbrainz.org/ws/2/recording/rec-2",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"ep-mbid\",\"title\":\"The EP\",\"primary-type\":\"EP\",\"first-release-date\":\"1996-07-01\"}}]}");

        var resolver2 = new SXMPlaylistAlbumResolver(httpClient2, LogManager.GetLogger("Test"));
        var resolution2 = resolver2.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")), albumsFirst);

        Assert("EP preferred over single in albums-first mode when no album exists", resolution2.Album == "The EP");
    }

    private static void TestAlbumResolutionRecordingSearchFindsAlbumAfterIsrcMiss()
    {
        Console.WriteLine("\n[Test] Recording search finds album releases after ISRC misses");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"We Might as Well Be Strangers\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[{\"id\":\"rec-1\",\"title\":\"We Might as Well Be Strangers\"," +
            "\"artist-credit\":[{\"artist\":{\"id\":\"weezer-mbid\",\"name\":\"Weezer\"}},{\"artist\":{\"id\":\"wednesday-mbid\",\"name\":\"Wednesday\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"weezer-mbid\",\"name\":\"Weezer\"}},{\"artist\":{\"id\":\"wednesday-mbid\",\"name\":\"Wednesday\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"weezer-mbid\",\"name\":\"Weezer\"}},{\"artist\":{\"id\":\"wednesday-mbid\",\"name\":\"Wednesday\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"We Might as Well Be Strangers\",\"primary-type\":\"Single\",\"first-release-date\":\"2026-06-03\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"weezer-mbid\",\"name\":\"Weezer\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"Weezer\",\"primary-type\":\"Album\",\"first-release-date\":\"2026-08-21\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Weezer", "We Might as Well Be Strangers", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("recording search selected the single for Singles priority", results[ReleasePriorityMode.Singles].AlbumMusicBrainzId == "single-mbid");
        Assert("recording search selected the album for Albums priority", results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == "album-mbid");
        Assert("multi-artist recording leaves artist MBID unset", results[ReleasePriorityMode.Singles].ArtistMusicBrainzId == null);
        Assert($"recording search avoided title-search fallback (calls: {httpClient.CallCount})", httpClient.CallCount == 4);
    }

    private static void TestAlbumResolutionRecordingSearchRunsWithoutProviderLinks()
    {
        Console.WriteLine("\n[Test] Recording search runs without Deezer or Apple links");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[{\"id\":\"rec-1\",\"title\":\"Song A\"," +
            "\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"Song A\",\"primary-type\":\"Single\",\"first-release-date\":\"2026-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"Album A\",\"primary-type\":\"Album\",\"first-release-date\":\"2026-08-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Artist One", "Song A", new Dictionary<string, string>());

        Assert("no-link recording search selected the single", results[ReleasePriorityMode.Singles].AlbumMusicBrainzId == "single-mbid");
        Assert("no-link recording search selected the album", results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == "album-mbid");
        Assert($"no-link path used one search plus one recording detail call (calls: {httpClient.CallCount})", httpClient.CallCount == 2);
    }

    private static void TestAlbumResolutionRecordingSearchAvoidsDuplicateAppleLookup()
    {
        Console.WriteLine("\n[Test] Recording search avoids duplicate Apple lookup when Deezer metadata resolves");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"album\":{\"title\":\"Song A\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[{\"id\":\"rec-1\",\"title\":\"Song A\"," +
            "\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"Song A\",\"primary-type\":\"Single\",\"first-release-date\":\"2026-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"Album A\",\"primary-type\":\"Album\",\"first-release-date\":\"2026-08-01\"}}]}");
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"Apple Album\"}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities(
            "Artist One",
            "Song A",
            Links(
                ("deezer", "https://www.deezer.com/track/624510"),
                ("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("recording search resolved both priorities", results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == "album-mbid");
        Assert("Apple lookup was skipped after MBID resolution", !httpClient.RequestUrls.Any(u => u.Contains("itunes.apple.com")));
    }

    private static void TestAlbumResolutionAppleLookupFailureIsNotRepeated()
    {
        Console.WriteLine("\n[Test] Apple lookup failure is not repeated for title-only fallback");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=", "{\"recordings\":[]}");
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities(
            "Artist One",
            "Song A",
            Links(("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("failed Apple lookup produced no fallback result", results.Count == 0);
        Assert("Apple lookup was attempted once", httpClient.RequestUrls.Count(u => u.Contains("itunes.apple.com")) == 1);
    }

    private static void TestAlbumResolutionRecordingSearchCapsDetailLookups()
    {
        Console.WriteLine("\n[Test] Recording search caps detail lookups");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[" +
            "{\"id\":\"rec-1\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]}," +
            "{\"id\":\"rec-2\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]}," +
            "{\"id\":\"rec-3\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]}," +
            "{\"id\":\"rec-4\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-2", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-3", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-4",
            "{\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"late-mbid\",\"title\":\"Late Album\",\"primary-type\":\"Album\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Artist One", "Song A", new Dictionary<string, string>());
        var detailCalls = httpClient.RequestUrls.Count(u => u.Contains("musicbrainz.org/ws/2/recording/rec-"));

        Assert("recording search stopped before the fourth detail lookup", detailCalls == 3);
        Assert("capped lookup did not use the fourth recording result", !results.ContainsKey(ReleasePriorityMode.Albums));
    }

    private static void TestAlbumResolutionRecordingSearchPrefersStudioRecording()
    {
        Console.WriteLine("\n[Test] Recording search prefers a later studio recording over earlier compilations");

        // MusicBrainz relevance lists compilations first; the studio-album recording sits at index 3,
        // beyond the 3-detail cap. Inline release metadata lets the resolver promote it before the cap.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[" +
            "{\"id\":\"rec-1\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"comp-1\",\"title\":\"Summer Hits\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"]}}]}," +
            "{\"id\":\"rec-2\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"comp-2\",\"title\":\"Best Of\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"]}}]}," +
            "{\"id\":\"rec-3\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"comp-3\",\"title\":\"Gold\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"]}}]}," +
            "{\"id\":\"rec-4\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"studio-mbid\",\"title\":\"The Studio Album\",\"primary-type\":\"Album\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-2", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-3", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-4",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"studio-mbid\",\"title\":\"The Studio Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1984-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Artist One", "Song A", new Dictionary<string, string>());

        Assert("studio recording was detail-fetched ahead of the compilations", httpClient.RequestUrls.Any(u => u.Contains("recording/rec-4")));
        Assert("singles priority resolved to the studio album", results.TryGetValue(ReleasePriorityMode.Singles, out var singles) && singles.AlbumMusicBrainzId == "studio-mbid");
        Assert("albums priority resolved to the studio album", results.TryGetValue(ReleasePriorityMode.Albums, out var albums) && albums.AlbumMusicBrainzId == "studio-mbid");
    }

    private static void TestAlbumResolutionRecordingSearchKeepsSingleCandidate()
    {
        Console.WriteLine("\n[Test] Recording search reorder keeps a single-carrying recording reachable");

        // A recording whose inline release is a Single (allowed primary type, no secondary types)
        // must still be promoted ahead of compilation-only recordings so Singles priority can pick it.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[" +
            "{\"id\":\"rec-1\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"comp-1\",\"title\":\"Hits\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"]}}]}," +
            "{\"id\":\"rec-2\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"single-mbid\",\"title\":\"Song A\",\"primary-type\":\"Single\"}}]}," +
            "{\"id\":\"rec-3\",\"title\":\"Song A\",\"artist-credit\":[{\"artist\":{\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"comp-2\",\"title\":\"Gold\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"]}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1", "{\"releases\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-2",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"Song A\",\"primary-type\":\"Single\",\"first-release-date\":\"1984-01-01\"}}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-3", "{\"releases\":[]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Artist One", "Song A", new Dictionary<string, string>());

        Assert("single-carrying recording was detail-fetched", httpClient.RequestUrls.Any(u => u.Contains("recording/rec-2")));
        Assert("singles priority selected the single", results.TryGetValue(ReleasePriorityMode.Singles, out var singles) && singles.AlbumMusicBrainzId == "single-mbid");
    }

    private static void TestAlbumResolutionFilterExcludesDisallowedRelease()
    {
        Console.WriteLine("\n[Test] A metadata profile filter drops release-groups of disallowed type/status");

        // Only Official/Studio/Album allowed: a Bootleg single exists but must be filtered out in
        // favor of the allowed Official album.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Bootleg\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"bootleg-single\",\"title\":\"Rare Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1990-01-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"official-album\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var filter = new AlbumTypeFilter(
            new HashSet<string> { "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")), filter);

        Assert("Bootleg single filtered out, Official album selected", resolution.Album == "The Album");
        Assert("selected MBID is the allowed album", resolution.AlbumMusicBrainzId == "official-album");

        // A filter that excludes everything -> falls through to Deezer title floor.
        var httpClient2 = new FakeHttpClient();
        httpClient2.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"No Code\"}}");
        httpClient2.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient2.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var noMatch = new AlbumTypeFilter(
            new HashSet<string> { "Single" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver2 = new SXMPlaylistAlbumResolver(httpClient2, LogManager.GetLogger("Test"));
        var resolution2 = resolver2.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")), noMatch);

        Assert("all-filtered-out falls through to Deezer title", resolution2.Album == "No Code");
        Assert("no MBID when everything filtered out", resolution2.AlbumMusicBrainzId == null);
    }

    private static void TestAlbumResolutionTitleSearchRecoversAfterIsrcMiss()
    {
        Console.WriteLine("\n[Test] After ISRC misses, a release title search recovers a real MBID");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"Three Cheers for Sweet Revenge\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"mcr-mbid\",\"name\":\"My Chemical Romance\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"Three Cheers for Sweet Revenge\",\"primary-type\":\"Album\",\"first-release-date\":\"2004-06-08\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("My Chemical Romance", "I'm Not Okay (I Promise)", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("title search recovered the album", resolution.Album == "Three Cheers for Sweet Revenge");
        Assert("album MBID attached", resolution.AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached", resolution.ArtistMusicBrainzId == "mcr-mbid");
    }

    private static void TestAlbumResolutionTitleSearchReturnsBothPrioritiesFromOneLookup()
    {
        Console.WriteLine("\n[Test] Title search ranks one result set for both release priorities");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"The Record\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Record\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Record\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities("Artist One", "Some Song", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("singles priority selected the single", results[ReleasePriorityMode.Singles].AlbumMusicBrainzId == "single-mbid");
        Assert("albums priority selected the album", results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == "album-mbid");
        Assert($"title-search dual ranking followed Deezer, ISRC, empty recording-search, and release-search sequence (calls: {httpClient.CallCount})", httpClient.CallCount == 4);
    }

    private static void TestAlbumResolutionEmptyDeezerResultFallsThroughToApple()
    {
        Console.WriteLine("\n[Test] Empty Deezer/MB results use Apple fallback for both priorities");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"Apple Album\"}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities(
            "Artist One",
            "Some Song",
            Links(
                ("deezer", "https://www.deezer.com/track/624510"),
                ("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("Apple filled singles priority with title floor", results[ReleasePriorityMode.Singles].Album == "Apple Album" && results[ReleasePriorityMode.Singles].AlbumMusicBrainzId == null);
        Assert("Apple filled albums priority with title floor", results[ReleasePriorityMode.Albums].Album == "Apple Album" && results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == null);
    }

    private static void TestAlbumResolutionTitleSearchRejectsWrongArtist()
    {
        Console.WriteLine("\n[Test] Title search rejects a high-score hit when the credited artist doesn't match");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"Blind\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        // Same-titled album but credited to a different artist ("Wild Horses / Blind" case).
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"wrong-mbid\",\"name\":\"Chuck Hammer\"}}]," +
            "\"release-group\":{\"id\":\"wrong-album-mbid\",\"title\":\"Blind on Blind\",\"primary-type\":\"Album\",\"first-release-date\":\"1976-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Sundays", "Wild Horses", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("wrong-artist release not attached", resolution.AlbumMusicBrainzId == null);
        Assert("falls through to Deezer title floor", resolution.Album == "Blind");
    }

    private static void TestAlbumResolutionTitleSearchRejectsOneTokenContainment()
    {
        Console.WriteLine("\n[Test] Title search rejects one-token self-titled matches inside longer provider titles");

        var mozartHttp = new FakeHttpClient();
        mozartHttp.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"Perahia & Mozart: Perfect Match\"}]}");
        mozartHttp.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"wrong-mozart-artist\",\"name\":\"moZart\"}}]," +
            "\"release-group\":{\"id\":\"wrong-mozart-album\",\"title\":\"moZart\",\"primary-type\":\"Album\",\"first-release-date\":\"1994-01-01\"}}]}");

        var mozartResolver = new SXMPlaylistAlbumResolver(mozartHttp, LogManager.GetLogger("Test"));
        var mozart = mozartResolver.Resolve("Mozart", "Piano Concerto No. 20 in D minor, K. 466", Links(("appleMusic", "https://geo.music.apple.com/us/album/_/1686922239?i=1686922505")));

        Assert("moZart self-titled MBID not attached", mozart.AlbumMusicBrainzId == null);
        Assert("falls through to the Apple album title", mozart.Album == "Perahia & Mozart: Perfect Match");

        var cakeHttp = new FakeHttpClient();
        cakeHttp.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM10100001\",\"album\":{\"title\":\"Cake: B-Sides and Rarities\"}}");
        cakeHttp.Respond("musicbrainz.org/ws/2/isrc/USSM10100001", "{\"recordings\":[]}");
        cakeHttp.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"wrong-cake-artist\",\"name\":\"Cake\"}}]," +
            "\"release-group\":{\"id\":\"wrong-cake-album\",\"title\":\"Cake\",\"primary-type\":\"Album\",\"first-release-date\":\"1992-01-01\"}}]}");

        var cakeResolver = new SXMPlaylistAlbumResolver(cakeHttp, LogManager.GetLogger("Test"));
        var cake = cakeResolver.Resolve("Cake", "Short Skirt/Long Jacket", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("Cake self-titled MBID not attached", cake.AlbumMusicBrainzId == null);
        Assert("falls through to the Deezer album title", cake.Album == "Cake: B-Sides and Rarities");
    }

    private static void TestAlbumResolutionTitleSearchViaApple()
    {
        Console.WriteLine("\n[Test] Title search upgrades an Apple Music title to a real MBID");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("itunes.apple.com/lookup", "{\"results\":[{\"collectionName\":\"No Code\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("appleMusic", "https://geo.music.apple.com/us/album/_/157478390?i=157478507")));

        Assert("Apple title upgraded to MBID", resolution.Album == "No Code");
        Assert("album MBID attached", resolution.AlbumMusicBrainzId == "album-mbid-1");
    }

    private static void TestAlbumResolutionRanksCompilationLast()
    {
        Console.WriteLine("\n[Test] Compilation-typed releases rank below studio albums in title-search selection");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"Greatest Hits\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        // Same artist: a compilation and a studio release both titled to match.
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"comp-mbid\",\"title\":\"Greatest Hits\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"],\"first-release-date\":\"2000-01-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"Greatest Hits\",\"primary-type\":\"Album\",\"first-release-date\":\"1990-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "Some Song", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("studio release preferred over compilation", resolution.AlbumMusicBrainzId == "album-mbid-1");
    }

    private static void TestAlbumResolutionRanksMultiTagCompilationLast()
    {
        Console.WriteLine("\n[Test] A compilation carrying an extra secondary type still ranks last (not escaped)");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"Greatest Hits\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        // Same artist, same title: a clean studio release vs a Compilation+Soundtrack tagged release.
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"comp-mbid\",\"title\":\"Greatest Hits\",\"primary-type\":\"Album\",\"secondary-types\":[\"Soundtrack\",\"Compilation\"],\"first-release-date\":\"2000-01-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"Greatest Hits\",\"primary-type\":\"Album\",\"first-release-date\":\"1990-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Artist One", "Some Song", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("studio release preferred even when compilation carries Soundtrack too", resolution.AlbumMusicBrainzId == "album-mbid-1");
    }

    private static void TestAlbumResolutionAllowsSameArtistCompilationFallback()
    {
        Console.WriteLine("\n[Test] Same-artist compilations are allowed as a metadata-profile fallback");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM10100001\",\"album\":{\"title\":\"B-Sides and Rarities\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM10100001", "{\"recordings\":[{\"id\":\"rec-cake\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-cake",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"release-group\":{\"id\":\"cake-bsides\",\"title\":\"B-Sides and Rarities\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"],\"first-release-date\":\"2007-08-14\"}}]}");

        var noCompilations = new AlbumTypeFilter(
            new HashSet<string> { "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("CAKE", "Short Skirt/Long Jacket", Links(("deezer", "https://www.deezer.com/track/624510")), noCompilations);

        Assert("same-artist compilation fallback selected", resolution.AlbumMusicBrainzId == "cake-bsides");
        Assert("recording artist MBID retained", resolution.ArtistMusicBrainzId == "cake-mbid");
    }

    private static void TestAlbumResolutionCompilationFallbackStillExcludesVariousArtists()
    {
        Console.WriteLine("\n[Test] Compilation fallback still excludes Various Artists release-groups");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM10100001\",\"album\":{\"title\":\"B-Sides and Rarities\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM10100001", "{\"recordings\":[{\"id\":\"rec-cake\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-cake",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"89ad4ac3-39f7-470e-963a-56509c546377\",\"name\":\"Various Artists\"}}]," +
            "\"release-group\":{\"id\":\"va-comp\",\"title\":\"Alternative Hits\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"],\"first-release-date\":\"2007-08-14\"}}]}");

        var noCompilations = new AlbumTypeFilter(
            new HashSet<string> { "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("CAKE", "Short Skirt/Long Jacket", Links(("deezer", "https://www.deezer.com/track/624510")), noCompilations);

        Assert("VA compilation fallback not selected", resolution.AlbumMusicBrainzId == null);
        Assert("falls through to Deezer title", resolution.Album == "B-Sides and Rarities");
    }

    private static void TestAlbumResolutionCompilationFallbackRequiresAllowedStatus()
    {
        Console.WriteLine("\n[Test] Compilation fallback does not rescue disallowed statuses");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM10100001\",\"album\":{\"title\":\"B-Sides and Rarities\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM10100001", "{\"recordings\":[{\"id\":\"rec-cake\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-cake",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"releases\":[{\"status\":\"Bootleg\",\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"release-group\":{\"id\":\"bootleg-comp\",\"title\":\"B-Sides and Rarities\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"],\"first-release-date\":\"2007-08-14\"}}]}");

        var officialOnly = new AlbumTypeFilter(
            new HashSet<string> { "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("CAKE", "Short Skirt/Long Jacket", Links(("deezer", "https://www.deezer.com/track/624510")), officialOnly);

        Assert("Bootleg compilation fallback not selected", resolution.AlbumMusicBrainzId == null);
        Assert("falls through to Deezer title", resolution.Album == "B-Sides and Rarities");
    }

    private static void TestAlbumResolutionTitleSearchAllowsSameArtistCompilationFallback()
    {
        Console.WriteLine("\n[Test] Title search can use same-artist compilation fallback");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM10100001\",\"album\":{\"title\":\"B-Sides and Rarities\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM10100001", "{\"recordings\":[]}");
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"cake-mbid\",\"name\":\"CAKE\"}}]," +
            "\"release-group\":{\"id\":\"cake-bsides\",\"title\":\"B-Sides and Rarities\",\"primary-type\":\"Album\",\"secondary-types\":[\"Compilation\"],\"first-release-date\":\"2007-08-14\"}}]}");

        var noCompilations = new AlbumTypeFilter(
            new HashSet<string> { "Album" },
            new HashSet<string> { "Studio" },
            new HashSet<string> { "Official" });

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("CAKE", "Short Skirt/Long Jacket", Links(("deezer", "https://www.deezer.com/track/624510")), noCompilations);

        Assert("title-search same-artist compilation fallback selected", resolution.AlbumMusicBrainzId == "cake-bsides");
        Assert("title-search artist MBID attached", resolution.ArtistMusicBrainzId == "cake-mbid");
    }

    private static void TestAlbumResolutionTitleSearchStripsEditionSuffixInQuery()
    {
        Console.WriteLine("\n[Test] Edition suffix in the Deezer title is stripped from the search query");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"Three Cheers for Sweet Revenge (Deluxe Edition)\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[]}");
        // Verify the query sent to MB does NOT contain the suffix - FakeHttpClient captures the URL.
        httpClient.Respond("musicbrainz.org/ws/2/release?query=",
            "{\"releases\":[{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"mcr-mbid\",\"name\":\"My Chemical Romance\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"Three Cheers for Sweet Revenge\",\"primary-type\":\"Album\",\"first-release-date\":\"2004-06-08\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("My Chemical Romance", "I'm Not Okay (I Promise)", Links(("deezer", "https://www.deezer.com/track/624510")));

        Assert("resolved despite the suffix in the source title", resolution.AlbumMusicBrainzId == "album-mbid-1");
        Assert("query did not contain the literal suffix", !httpClient.LastRequestUrl.Contains("Deluxe"));
    }

    private static void TestAlbumResolutionRecordingSearchStripsYearSuffix()
    {
        Console.WriteLine("\n[Test] Recording search strips the SiriusXM year annotation from the song title");

        var httpClient = new FakeHttpClient();
        // No provider links -> recording search is the only path. The played title carries the
        // "(85)" year annotation; MB's clean title is just "Emergency".
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[{\"id\":\"rec-1\",\"title\":\"Emergency\"," +
            "\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"Emergency\",\"primary-type\":\"Album\",\"first-release-date\":\"1984-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var resolution = resolver.Resolve("Kool & The Gang", "Emergency (85)", new Dictionary<string, string>());

        Assert("year-suffixed title matched the clean MusicBrainz recording", resolution.AlbumMusicBrainzId == "album-mbid");
        Assert("query did not contain the literal year annotation", !httpClient.LastRequestUrl.Contains("(85)"));
    }

    private static void TestAlbumResolutionRecordingSearchUsesDeezerArtistForFragmentedNames()
    {
        Console.WriteLine("\n[Test] Recording search tries the Deezer artist name when the feed fragmented the artist");

        // Feed splits "Kool & The Gang" into ['Kool', 'The Gang']; only "Kool" is passed as the
        // primary artist. Deezer's track payload has the canonical name, which must be tried.
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api.deezer.com/track/624510", "{\"artist\":{\"name\":\"Kool & The Gang\"},\"album\":{\"title\":\"Celebration Live\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/recording?query=",
            "{\"recordings\":[{\"id\":\"rec-1\",\"title\":\"Emergency\"," +
            "\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"kool-mbid\",\"name\":\"Kool & The Gang\"}}]," +
            "\"release-group\":{\"id\":\"emergency-album\",\"title\":\"Emergency\",\"primary-type\":\"Album\",\"first-release-date\":\"1984-01-01\"}}]}");

        var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
        var results = resolver.ResolveAllPriorities(
            "Kool",
            "Emergency (85)",
            Links(("deezer", "https://www.deezer.com/track/624510")),
            null,
            new List<string> { "Kool", "The Gang" });

        Assert("fragmented artist resolved via the Deezer canonical name", results[ReleasePriorityMode.Albums].AlbumMusicBrainzId == "emergency-album");
        Assert("recording search ran once on the canonical artist", httpClient.RequestUrls.Count(u => u.Contains("musicbrainz.org/ws/2/recording?query=")) == 1);
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

        var originalBackoff = SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff;
        SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff = TimeSpan.Zero;
        try
        {
            var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
            var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

            Assert("album resolved after retrying the busy MusicBrainz response", resolution.Album == "No Code");
            Assert($"ISRC endpoint hit twice (503 then OK) - total calls {httpClient.CallCount}", httpClient.CallCount == 4);
        }
        finally
        {
            SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff = originalBackoff;
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

        var originalBackoff = SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff;
        SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff = TimeSpan.Zero;
        try
        {
            var resolver = new SXMPlaylistAlbumResolver(httpClient, LogManager.GetLogger("Test"));
            var resolution = resolver.Resolve("Artist One", "I'm Open", Links(("deezer", "https://www.deezer.com/track/624510")));

            Assert("not resolved after repeated 503s", resolution.Album == null);
            Assert($"tried three ISRC calls, then attempted recording search (calls: {httpClient.CallCount})", httpClient.CallCount == 5);
        }
        finally
        {
            SXMPlaylistAlbumResolver.MusicBrainzRetryBackoff = originalBackoff;
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

    private static void TestStoreRecordsRepeatedPlayEvents()
    {
        Console.WriteLine("\n[Test] PlayEvents keep repeated airings while deduping exact feed replays");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        Assert("first play event is new", store.TryRecordPlayEvent("p1", "altnation", "track1", "Artist One", "Song A", now, null));
        Assert("exact same play event is deduped", !store.TryRecordPlayEvent("p1", "altnation", "track1", "Artist One", "Song A", now, null));
        Assert("same play id at a different timestamp is retained as a repeated event", store.TryRecordPlayEvent("p1", "altnation", "track1", "Artist One", "Song A", now.AddHours(1), null));

        var events = store.GetPlayEvents("altnation", now.AddMinutes(-1), now.AddHours(2));
        Assert($"two play events retained (got {events.Count})", events.Count == 2);
    }

    private static void TestStoreMigratesLegacyPlaysToPlayEvents()
    {
        Console.WriteLine("\n[Test] Legacy Plays rows are copied into PlayEvents on initialization");

        var folder = NewFolder();
        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        Directory.CreateDirectory(Path.Combine(folder.AppDataFolder, "SXMPlaylist"));
        var now = DateTime.UtcNow;

        using (var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath};Version=3;"))
        {
            connection.Open();
            using var create = new System.Data.SQLite.SQLiteCommand(
                "CREATE TABLE Plays (PlayId TEXT NOT NULL, Channel TEXT NOT NULL, Artist TEXT NOT NULL, Song TEXT NOT NULL, TimestampUtc TEXT NOT NULL, PRIMARY KEY (PlayId, Artist))",
                connection);
            create.ExecuteNonQuery();

            using var insert = new System.Data.SQLite.SQLiteCommand(
                "INSERT INTO Plays (PlayId, Channel, Artist, Song, TimestampUtc) VALUES ('legacy1', 'altnation', 'Legacy Artist', 'Legacy Song', @timestamp)",
                connection);
            insert.Parameters.AddWithValue("@timestamp", now.ToString("O"));
            insert.ExecuteNonQuery();
        }

        var store = new SXMPlaylistHistoryStore(folder);
        var events = store.GetPlayEvents("altnation", now.AddMinutes(-1), now.AddMinutes(1));

        Assert("legacy play became a play event", events.Count == 1 && events[0].PlayId == "legacy1");
        Assert("legacy play has unknown show attribution", events[0].ProgramId == null && events[0].ShowName == null);
    }

    private static void TestStoreAssociatesPlayEventsWithShowWindows()
    {
        Console.WriteLine("\n[Test] PlayEvents can carry persisted show-window attribution");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;
        store.SaveShowWindows("altnation", new[]
        {
            new ShowInfo("16824", "Alt-18", new[] { new ShowWindow(now.AddMinutes(-10), now.AddMinutes(50)) })
        });

        var showWindow = store.GetShowWindowForPlay("altnation", now);
        Assert("show window found for play timestamp", showWindow?.ProgramId == "16824" && showWindow.ShowName == "Alt-18");

        store.TryRecordPlayEvent("p1", "altnation", "track1", "Artist One", "Song A", now, showWindow);
        var events = store.GetPlayEvents("altnation", now.AddMinutes(-1), now.AddMinutes(1), "16824");

        Assert("event is queryable by persisted show id", events.Count == 1 && events[0].ShowName == "Alt-18");
        Assert("event stores show window bounds", events[0].ShowStartUtc != null && events[0].ShowEndUtc != null);
    }

    private static void TestStorePlayEventsCanBeQueriedByRangeAndShow()
    {
        Console.WriteLine("\n[Test] PlayEvents support playlist time-range and show filtering");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;
        store.SaveShowWindows("altnation", new[]
        {
            new ShowInfo("show1", "Show One", new[] { new ShowWindow(now.AddHours(-3), now.AddHours(-1)) }),
            new ShowInfo("show2", "Show Two", new[] { new ShowWindow(now.AddHours(-1), now.AddHours(1)) })
        });

        var oldShow = store.GetShowWindowForPlay("altnation", now.AddHours(-2));
        var currentShow = store.GetShowWindowForPlay("altnation", now.AddMinutes(-30));
        store.TryRecordPlayEvent("old", "altnation", "track-old", "Artist", "Old Song", now.AddHours(-2), oldShow);
        store.TryRecordPlayEvent("new", "altnation", "track-new", "Artist", "New Song", now.AddMinutes(-30), currentShow);

        var lastHour = store.GetPlayEvents("altnation", now.AddHours(-1), now.AddMinutes(1));
        var show2 = store.GetPlayEvents("altnation", now.AddHours(-3), now.AddMinutes(1), "show2");

        Assert("24h/1h-style range query only returns rows in range", lastHour.Count == 1 && lastHour[0].PlayId == "new");
        Assert("show filter returns only matching show rows", show2.Count == 1 && show2[0].PlayId == "new");
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

        var presentable = store.GetPresentableTracks("altnation", now - SXMPlaylistHistoryStore.PresentationWindow, 10);
        Assert("resolved track is presentable", presentable.Count == 1 && presentable[0].Album == "No Code");
        Assert("artist MBID carried for single-artist track", presentable[0].ArtistMusicBrainzId == "artist-mbid-1");
        Assert("not presentable for another channel", store.GetPresentableTracks("lithium", now - SXMPlaylistHistoryStore.PresentationWindow, 10).Count == 0);
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

    private static void TestStorePersistsResolutionTraceIdentifiers()
    {
        Console.WriteLine("\n[Test] Store persists trace identifiers from track resolution");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", "artist-mbid-1", "album-mbid-1", "recording-mbid-1", "USSM19601763", "isrc"), now);

        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        using var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        using var command = new SQLiteCommand("SELECT RecordingMusicBrainzId, Isrc, ResolutionMethod FROM Tracks WHERE TrackId = 'track1'", connection);
        using var reader = command.ExecuteReader();

        Assert("trace row exists", reader.Read());
        Assert("recording MBID persisted", reader.GetString(0) == "recording-mbid-1");
        Assert("ISRC persisted", reader.GetString(1) == "USSM19601763");
        Assert("resolution method persisted", reader.GetString(2) == "isrc");
    }

    private static void TestStoreRecordsPlexPlaylistTrackMatches()
    {
        Console.WriteLine("\n[Test] Store records Plex playlist track match audit rows");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var syncUtc = DateTime.UtcNow;
        var playUtc = syncUtc.AddMinutes(-5);

        store.RecordPlexPlaylistTrackMatches(42, syncUtc, new[]
        {
            new PlexPlaylistTrackMatchRecord(
                "play1",
                "track1",
                "altnation",
                "Kool & The Gang",
                "Celebration",
                playUtc,
                "recording-mbid-1",
                "USDE12345678",
                "100",
                "Kool & The Gang",
                "Celebration",
                "Celebrate!",
                "mbid://plex-guid-1",
                "exact-title-artist",
                "high")
        });

        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        using var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        using var command = new SQLiteCommand("SELECT ListId, PlayId, SxmTrackId, RecordingMusicBrainzId, Isrc, PlexRatingKey, PlexGuid, MatchMethod, Confidence FROM PlexPlaylistTrackMatches", connection);
        using var reader = command.ExecuteReader();

        Assert("audit row exists", reader.Read());
        Assert("list id persisted", reader.GetInt64(0) == 42);
        Assert("play id persisted", reader.GetString(1) == "play1");
        Assert("SXM track id persisted", reader.GetString(2) == "track1");
        Assert("recording MBID persisted in audit", reader.GetString(3) == "recording-mbid-1");
        Assert("ISRC persisted in audit", reader.GetString(4) == "USDE12345678");
        Assert("Plex rating key persisted", reader.GetString(5) == "100");
        Assert("Plex guid persisted", reader.GetString(6) == "mbid://plex-guid-1");
        Assert("match method persisted", reader.GetString(7) == "exact-title-artist");
        Assert("confidence persisted", reader.GetString(8) == "high");
    }

    private static void TestStoreReadsLegacyPlexTrackCache()
    {
        Console.WriteLine("\n[Test] Store reads legacy string-only Plex track cache rows");

        var folder = NewFolder();
        _ = new SXMPlaylistHistoryStore(folder);
        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        using (var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;"))
        {
            connection.Open();
            using var command = new SQLiteCommand(
                "INSERT INTO PlexPlaylistState (ListId, PlaylistTitle, PlaylistRatingKey, LastSyncUtc, TrackCacheJson, UserPlaylistKeysJson) " +
                "VALUES (42, 'SXM Alt Nation', '500', @utc, '{\"Kool & The Gang||Celebration\":\"100\"}', '{}')",
                connection);
            command.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var store = new SXMPlaylistHistoryStore(folder);
        var state = store.GetPlexPlaylistState(42);

        Assert("legacy cache state read", state != null);
        Assert("legacy rating key preserved", state!.TrackCache["Kool & The Gang||Celebration"].RatingKey == "100");
        Assert("legacy cache confidence marked unknown", state.TrackCache["Kool & The Gang||Celebration"].Confidence == "unknown");
    }

    private static void TestStorePresentableWindowExpires()
    {
        Console.WriteLine("\n[Test] A resolved track falls out of the presentation window after 25 hours");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, "album-mbid-1"), now - SXMPlaylistHistoryStore.PresentationWindow - TimeSpan.FromMinutes(1));

        Assert("old resolution is no longer presentable", store.GetPresentableTracks("altnation", now - SXMPlaylistHistoryStore.PresentationWindow, 10).Count == 0);
    }

    private static void TestStoreRequireMbidFiltersBeforeLimit()
    {
        Console.WriteLine("\n[Test] Require MusicBrainz ID filters before the presentation limit");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 25; i++)
        {
            var trackId = "titleOnly" + i;
            store.UpsertTrack(trackId, "altnation", new[] { "Artist" }, "Title Only " + i, null, null, now.AddMinutes(i));
            store.MarkTrackResolved(trackId, new AlbumResolution(true, "Title Album", null, null), now.AddMinutes(i));
        }

        store.UpsertTrack("mbidTrack", "altnation", new[] { "Artist" }, "MBID Song", null, null, now.AddMinutes(-5));
        store.MarkTrackResolved("mbidTrack", new AlbumResolution(true, "MBID Album", null, "album-mbid"), now.AddMinutes(-5));

        var strict = store.GetPresentableTracks("altnation", now.AddHours(-1), 20, requireMusicBrainzId: true);

        Assert("older MBID row is returned even when newer title-only rows exceed limit", strict.Count == 1 && strict[0].TrackId == "mbidTrack");
    }

    private static void TestStoreMinimumPlaysFiltersBeforeLimit()
    {
        Console.WriteLine("\n[Test] Minimum Plays filters before the presentation limit");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 25; i++)
        {
            var trackId = "singlePlay" + i;
            store.TryRecordPlay("singlePlay" + i, "altnation", "Artist", "Single Play " + i, now.AddMinutes(i));
            store.UpsertTrack(trackId, "altnation", new[] { "Artist" }, "Single Play " + i, null, null, now.AddMinutes(i));
            store.MarkTrackResolved(trackId, new AlbumResolution(true, "Single Album", null, "single-mbid" + i), now.AddMinutes(i));
        }

        store.TryRecordPlay("repeat1", "altnation", "Artist", "Repeat Song", now.AddMinutes(-10));
        store.TryRecordPlay("repeat2", "altnation", "Artist", "Repeat Song", now.AddMinutes(-9));
        store.UpsertTrack("repeatTrack", "altnation", new[] { "Artist" }, "Repeat Song", null, null, now.AddMinutes(-10));
        store.MarkTrackResolved("repeatTrack", new AlbumResolution(true, "Repeat Album", null, "repeat-mbid"), now.AddMinutes(-10));

        var presentable = store.GetPresentableTracks("altnation", now.AddHours(-1), 20, minimumPlays: 2);

        Assert("older repeated row is returned while newer single-play rows are hidden", presentable.Count == 1 && presentable[0].TrackId == "repeatTrack");
    }

    private static void TestStoreLimitCapsPresentationRows()
    {
        Console.WriteLine("\n[Test] Presentation limit caps the presented row count");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        for (var i = 0; i < 5; i++)
        {
            var trackId = "track" + i;
            store.UpsertTrack(trackId, "altnation", new[] { "Artist" }, "Song " + i, null, null, now.AddMinutes(i));
            store.MarkTrackResolved(trackId, new AlbumResolution(true, "Album " + i, null, "album-mbid" + i), now.AddMinutes(i));
        }

        var presentable = store.GetPresentableTracks("altnation", now.AddHours(-1), 3);

        Assert($"presentation is limited to 3 rows (got {presentable.Count})", presentable.Count == 3);
    }

    private static void TestStorePruneRemovesOldData()
    {
        Console.WriteLine("\n[Test] Prune drops plays and tracks older than the retention window");

        var store = NewHistoryStore();
        var old = DateTime.UtcNow - SXMPlaylistHistoryStore.PlayRetention - TimeSpan.FromDays(1);
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

    private static void TestStorePruneRemovesOldPlayEventsAndShowWindows()
    {
        Console.WriteLine("\n[Test] Prune drops old play events and show windows after the retention window");

        var store = NewHistoryStore();
        var old = DateTime.UtcNow - SXMPlaylistHistoryStore.PlayRetention - TimeSpan.FromDays(1);
        var fresh = DateTime.UtcNow;
        store.SaveShowWindows("altnation", new[]
        {
            new ShowInfo("old-show", "Old Show", new[] { new ShowWindow(old.AddHours(-1), old.AddHours(1)) }),
            new ShowInfo("new-show", "New Show", new[] { new ShowWindow(fresh.AddHours(-1), fresh.AddHours(1)) })
        });

        var oldShow = store.GetShowWindowForPlay("altnation", old);
        var freshShow = store.GetShowWindowForPlay("altnation", fresh);
        store.TryRecordPlayEvent("old", "altnation", "track-old", "Artist", "Old Song", old, oldShow);
        store.TryRecordPlayEvent("new", "altnation", "track-new", "Artist", "New Song", fresh, freshShow);

        store.Prune();

        var allEvents = store.GetPlayEvents("altnation", DateTime.MinValue, DateTime.UtcNow.AddDays(1));
        Assert("old play event pruned", allEvents.All(e => e.PlayId != "old"));
        Assert("fresh play event kept", allEvents.Any(e => e.PlayId == "new"));
        Assert("old show window pruned", store.GetShowWindowForPlay("altnation", old) == null);
        Assert("fresh show window kept", store.GetShowWindowForPlay("altnation", fresh)?.ProgramId == "new-show");
    }

    private static void TestStorePruneRemovesOldPlexPlaylistTrackMatches()
    {
        Console.WriteLine("\n[Test] Prune drops old Plex playlist audit rows after the retention window");

        var folder = NewFolder();
        var store = new SXMPlaylistHistoryStore(folder);
        var now = DateTime.UtcNow;
        var oldSync = now - SXMPlaylistHistoryStore.PlayRetention - TimeSpan.FromDays(1);
        var freshSync = now - TimeSpan.FromDays(1);

        store.RecordPlexPlaylistTrackMatches(42, oldSync, new[]
        {
            new PlexPlaylistTrackMatchRecord("old", null, "altnation", "Artist", "Old Song", oldSync, null, null, null, null, null, null, null, "unmatched", "none")
        });
        store.RecordPlexPlaylistTrackMatches(42, freshSync, new[]
        {
            new PlexPlaylistTrackMatchRecord("fresh", null, "altnation", "Artist", "Fresh Song", freshSync, null, null, "100", "Artist", "Fresh Song", null, null, "exact-title-artist", "high")
        });

        store.Prune();

        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        using var connection = new SQLiteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        using var command = new SQLiteCommand("SELECT PlayId FROM PlexPlaylistTrackMatches ORDER BY PlayId", connection);
        using var reader = command.ExecuteReader();

        Assert("fresh audit row kept", reader.Read() && reader.GetString(0) == "fresh");
        Assert("old audit row pruned", !reader.Read());
    }

    private static void TestStoreHistoryRetentionFiltersQueryOnly()
    {
        Console.WriteLine("\n[Test] History retention days filters query results only");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;
        var twoDaysOld = now.AddDays(-2);

        store.TryRecordPlay("oldPlay", "altnation", "Artist", "Old Song", twoDaysOld);
        store.UpsertTrack("oldTrack", "altnation", new[] { "Artist" }, "Old Song", null, null, twoDaysOld);
        store.MarkTrackResolved("oldTrack", new AlbumResolution(true, "Old Album", null, "old-mbid"), now);

        var oneDayList = store.GetPresentableTracks("altnation", now.AddHours(-1), now.AddDays(-1), 10);
        var threeDayList = store.GetPresentableTracks("altnation", now.AddHours(-1), now.AddDays(-3), 10);
        store.Prune();

        Assert("one-day query hides the old play", oneDayList.Count == 0);
        Assert("three-day query includes the old play", threeDayList.Any(t => t.TrackId == "oldTrack"));
        Assert("global retention prune keeps the old play", store.GetPlays("altnation", DateTime.MinValue).Count == 1);
    }

    private static void TestStoreSchedulesRetryForNoMbidTrack()
    {
        Console.WriteLine("\n[Test] A no-MBID resolution schedules a retry 12h out, not immediately");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), now);

        Assert("not due yet within the 12h window", store.GetDueRetries(10, now).Count == 0);
        Assert("due after the retry interval", store.GetDueRetries(10, now + SXMPlaylistHistoryStore.RetryInterval + TimeSpan.FromMinutes(1)).Any(t => t.TrackId == "track1"));
    }

    private static void TestStoreRetryGivesUpAfterMaxAttempts()
    {
        Console.WriteLine("\n[Test] no-MBID retry stops after exhausting the attempt budget");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), now);

        for (var i = 0; i < SXMPlaylistHistoryStore.MaxRetryAttempts; i++)
        {
            store.RecordRetryFailure("track1", now);
        }

        Assert("excluded once attempts exhausted", !store.GetDueRetries(10, now + TimeSpan.FromDays(10)).Any(t => t.TrackId == "track1"));
    }

    private static void TestStoreRetrySuccessClearsRetryState()
    {
        Console.WriteLine("\n[Test] A successful retry that finds an MBID clears the retry schedule");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), now);
        store.RecordRetryFailure("track1", now);

        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", "artist-mbid-1", "album-mbid-1"), now + TimeSpan.FromHours(1));

        Assert("MBID track no longer due for retry", !store.GetDueRetries(10, now + TimeSpan.FromDays(1)).Any(t => t.TrackId == "track1"));
        Assert("still presentable after renewal", store.GetPresentableTracks("altnation", now + TimeSpan.FromHours(1) - SXMPlaylistHistoryStore.PresentationWindow, 10).Any(t => t.TrackId == "track1"));
    }

    private static void TestStoreNewPlayResetsRetryClock()
    {
        Console.WriteLine("\n[Test] A fresh play of a no-MBID track resets its retry clock");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), now);
        store.RecordRetryFailure("track1", now);

        // A later replay re-inserts the track: retry clock resets, immediately due again.
        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now + TimeSpan.FromHours(1));

        Assert("replay makes it due again immediately", store.GetDueRetries(10, now + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1)).Any(t => t.TrackId == "track1"));
    }

    private static void TestStoreRetryFailureRenewsPresentationWindow()
    {
        Console.WriteLine("\n[Test] A failed retry renews ResolvedUtc so the track stays presentable");

        var store = NewHistoryStore();
        var now = DateTime.UtcNow;

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, now);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), now);

        // Fail a retry past the 25h presentation window; the track-resolution row must be renewed
        // too, because presentation now joins the per-priority resolution cache.
        var retryFailureTime = now + SXMPlaylistHistoryStore.PresentationWindow + TimeSpan.FromHours(1);
        store.RecordRetryFailure("track1", retryFailureTime);

        Assert("track still presentable after failed retry renewed the window", store.GetPresentableTracks("altnation", retryFailureTime - SXMPlaylistHistoryStore.PresentationWindow, 10).Any(t => t.TrackId == "track1"));
    }

    private static void TestStoreMigrationAddsRetryColumnsIdempotently()
    {
        Console.WriteLine("\n[Test] Creating the store on an old-schema DB adds the retry columns without error");

        var folder = NewFolder();
        var dbPath = Path.Combine(folder.AppDataFolder, "SXMPlaylist", "history.db");
        Directory.CreateDirectory(Path.Combine(folder.AppDataFolder, "SXMPlaylist"));

        // Build an old-schema Tracks table manually (no NextRetryUtc / RetryAttempts), with an
        // existing no-MBID row that pre-dates the feature.
        using (var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath};Version=3;"))
        {
            connection.Open();
            using var command = new System.Data.SQLite.SQLiteCommand(
                "CREATE TABLE Tracks (TrackId TEXT PRIMARY KEY, Channel TEXT NOT NULL, ArtistsJson TEXT NOT NULL, " +
                "Song TEXT NOT NULL, DeezerUrl TEXT, AppleMusicUrl TEXT, TimestampUtc TEXT NOT NULL, " +
                "Resolved INTEGER NOT NULL DEFAULT 0, Failures INTEGER NOT NULL DEFAULT 0, Album TEXT, " +
                "ArtistMusicBrainzId TEXT, AlbumMusicBrainzId TEXT, ResolvedUtc TEXT)",
                connection);
            command.ExecuteNonQuery();

            using var insert = new System.Data.SQLite.SQLiteCommand(
                "INSERT INTO Tracks (TrackId, Channel, ArtistsJson, Song, TimestampUtc, Resolved, Album, ResolvedUtc) " +
                "VALUES ('legacy', 'altnation', '[\"Old Artist\"]', 'Old Song', @ts, 1, 'Old Album', @ts)",
                connection);
            insert.Parameters.AddWithValue("@ts", DateTime.UtcNow.AddDays(-3).ToString("O"));
            insert.ExecuteNonQuery();
        }

        var store = new SXMPlaylistHistoryStore(folder);

        // The migration backfill must stagger the legacy no-MBID row out by a full retry interval
        // (not make it immediately due), so a rollout doesn't flood MusicBrainz.
        Assert("legacy no-MBID row not immediately due after migration", !store.GetDueRetries(10, DateTime.UtcNow).Any(t => t.TrackId == "legacy"));
        Assert("legacy no-MBID row due after one interval", store.GetDueRetries(10, DateTime.UtcNow + SXMPlaylistHistoryStore.RetryInterval + TimeSpan.FromMinutes(1)).Any(t => t.TrackId == "legacy"));

        store.UpsertTrack("track1", "altnation", new[] { "Artist One" }, "Song A", null, null, DateTime.UtcNow);
        store.MarkTrackResolved("track1", new AlbumResolution(true, "No Code", null, null), DateTime.UtcNow);

        Assert("retry columns usable on migrated DB", store.GetDueRetries(10, DateTime.UtcNow + SXMPlaylistHistoryStore.RetryInterval).Any(t => t.TrackId == "track1"));

        // Constructing the store again (every start) must not throw, and must not re-stagger rows.
        var store2 = new SXMPlaylistHistoryStore(folder);
        Assert("second initialize is idempotent", store2.GetDueTracks(10).Count == 0);
    }

    private static void TestWorkerCapturesDueChannel()
    {
        Console.WriteLine("\n[Test] Worker captures a channel that has never been captured");
        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        Assert("play was recorded", store.GetPlays("altnation", DateTime.MinValue).Count == 1);
        Assert("last capture recorded", store.GetLastCaptureUtc("altnation") != null);
        Assert("feed and EPG requests made", httpClient.RequestUrls.Count(u => u.Contains("api/station/altnation")) == 1 && httpClient.RequestUrls.Count(u => u.Contains("sxmepg")) == 1);
    }

    private static void TestWorkerCapturesPlayEventShowAttribution()
    {
        Console.WriteLine("\n[Test] Worker stores captured play events with show attribution when EPG is available");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));
        httpClient.Respond("sxmepg", "{\"chEpgInfo\":{\"dayChSchedules\":[{\"episode\":[" +
            "{\"pgid\":\"show1\",\"pr\":{\"pName\":\"Show One\"},\"sc\":{\"sTimeStr\":\"08.04.2026 19:00 EDT\",\"eTimeStr\":\"08.04.2026 21:00 EDT\"}}" +
            "]}],\"pg\":[]}}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var events = store.GetPlayEvents("altnation", new DateTime(2026, 8, 4, 23, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc), "show1");

        Assert("captured event is attributed to the EPG show", events.Count == 1 && events[0].ShowName == "Show One");
    }

    private static void TestWorkerRecordsPlayEventsWhenEpgFails()
    {
        Console.WriteLine("\n[Test] Worker still records play events when EPG refresh fails");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var events = store.GetPlayEvents("altnation", DateTime.MinValue, DateTime.UtcNow.AddYears(10));

        Assert("play event recorded despite EPG 404", events.Count == 1 && events[0].PlayId == "play1");
        Assert("show attribution is unknown when EPG failed", events[0].ProgramId == null && events[0].ShowName == null);
    }

    private static void TestWorkerReusesFreshShowWindowsForDailyEpgRefresh()
    {
        Console.WriteLine("\n[Test] Worker reuses fresh show windows for 24h EPG refresh cadence");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));
        httpClient.Respond("sxmepg", "{\"chEpgInfo\":{\"dayChSchedules\":[{\"episode\":[" +
            "{\"pgid\":\"show1\",\"pr\":{\"pName\":\"Show One\"},\"sc\":{\"sTimeStr\":\"08.04.2026 19:00 EDT\",\"eTimeStr\":\"08.06.2026 21:00 EDT\"}}" +
            "]}],\"pg\":[]}}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");
        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));

        worker.RunOnce(CancellationToken.None);
        var epgRequestsAfterFirstCapture = httpClient.RequestUrls.Count(u => u.Contains("sxmepg"));

        var store = new SXMPlaylistHistoryStore(folder);
        store.SetLastCaptureUtc("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.CaptureInterval - TimeSpan.FromMinutes(1));
        worker.RunOnce(CancellationToken.None);

        var epgRequestsAfterSecondCapture = httpClient.RequestUrls.Count(u => u.Contains("sxmepg"));
        var events = store.GetPlayEvents("altnation", new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc), "show1");

        Assert("first due capture fetched EPG", epgRequestsAfterFirstCapture == 1);
        Assert("second due capture within 24h did not refetch EPG", epgRequestsAfterSecondCapture == 1);
        Assert("cached show window still attributes captured play", events.Count == 1 && events[0].ShowName == "Show One");
    }

    private static void TestWorkerSkipsCaptureWhenNotDue()
    {
        Console.WriteLine("\n[Test] Worker skips a channel whose capture is not due yet");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "Song A", Array.Empty<(string, string)>())));

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation");

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);
        var feedCallsAfterFirst = httpClient.RequestUrls.Count(u => u.Contains("api/station/altnation"));
        worker.RunOnce(CancellationToken.None);

        var feedCallsAfterSecond = httpClient.RequestUrls.Count(u => u.Contains("api/station/altnation"));
        Assert($"no additional feed request on the second pass (before {feedCallsAfterFirst}, after {feedCallsAfterSecond})", feedCallsAfterSecond == feedCallsAfterFirst);
    }

    private static void TestWorkerResolvesDueTracks()
    {
        Console.WriteLine("\n[Test] Worker resolves captured tracks and they become presentable");

        SXMPlaylistFeedCache.Clear();
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

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var presentable = store.GetPresentableTracks("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.PresentationWindow, 10, releasePriority: ReleasePriorityMode.Albums);

        Assert("captured + resolved track is presentable", presentable.Count == 1);
        Assert("correct album resolved", presentable[0].Album == "No Code");
        Assert("album MBID attached", presentable[0].AlbumMusicBrainzId == "album-mbid-1");
        Assert("artist MBID attached (single-artist)", presentable[0].ArtistMusicBrainzId == "artist-mbid-1");
    }

    private static void TestWorkerUsesListMetadataProfileForResolution()
    {
        Console.WriteLine("\n[Test] Worker applies the channel's list metadata profile when resolving");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "I'm Open", new[] { ("deezer", "https://www.deezer.com/track/624510") })));
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\",\"album\":{\"title\":\"No Code\"}}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[{\"status\":\"Official\",\"release-group\":{\"id\":\"album-mbid-1\",\"title\":\"No Code\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation", metadataProfileId: 42);

        // Profile 42 only allows Singles - the Official Album candidate must be filtered out, so the
        // track falls through to the Deezer-title floor with no MBID.
        var singlesOnly = new MetadataProfile
        {
            Id = 42,
            PrimaryAlbumTypes = new List<ProfilePrimaryAlbumTypeItem>
            {
                new() { Allowed = true, PrimaryAlbumType = PrimaryAlbumType.Single },
                new() { Allowed = false, PrimaryAlbumType = PrimaryAlbumType.Album }
            },
            SecondaryAlbumTypes = new List<ProfileSecondaryAlbumTypeItem>
            {
                new() { Allowed = true, SecondaryAlbumType = SecondaryAlbumType.Studio }
            },
            ReleaseStatuses = new List<ProfileReleaseStatusItem>
            {
                new() { Allowed = true, ReleaseStatus = ReleaseStatus.Official }
            }
        };

        var profiles = new FakeMetadataProfileService(singlesOnly);
        var worker = new SXMPlaylistWorker(httpClient, folder, factory, profiles, new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var presentable = store.GetPresentableTracks("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.PresentationWindow, 10, releasePriority: ReleasePriorityMode.Albums);

        Assert("track still resolved via Deezer title fallback", presentable.Count == 1);
        Assert("album title kept from Deezer", presentable[0].Album == "No Code");
        Assert("no MBID because profile excluded the only MB candidate", presentable[0].AlbumMusicBrainzId == null);
    }

    private static void TestWorkerUsesListReleasePriorityForResolution()
    {
        Console.WriteLine("\n[Test] Worker applies the channel's list release priority when resolving");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "I'm Open", new[] { ("deezer", "https://www.deezer.com/track/624510") })));
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation", metadataProfileId: 42, releasePriority: ReleasePriorityMode.Albums);

        var profile = new MetadataProfile
        {
            Id = 42,
            PrimaryAlbumTypes = new List<ProfilePrimaryAlbumTypeItem>
            {
                new() { Allowed = true, PrimaryAlbumType = PrimaryAlbumType.Single },
                new() { Allowed = true, PrimaryAlbumType = PrimaryAlbumType.Album }
            },
            SecondaryAlbumTypes = new List<ProfileSecondaryAlbumTypeItem>
            {
                new() { Allowed = true, SecondaryAlbumType = SecondaryAlbumType.Studio }
            },
            ReleaseStatuses = new List<ProfileReleaseStatusItem>
            {
                new() { Allowed = true, ReleaseStatus = ReleaseStatus.Official }
            }
        };

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(profile), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var presentable = store.GetPresentableTracks("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.PresentationWindow, 10, releasePriority: ReleasePriorityMode.Albums);

        Assert("album-first list selected the album", presentable.Count == 1 && presentable[0].Album == "The Album");
        Assert("album-first list used album MBID", presentable[0].AlbumMusicBrainzId == "album-mbid");
    }

    private static void TestWorkerStoresBothReleasePrioritiesForSharedChannel()
    {
        Console.WriteLine("\n[Test] Worker stores both release priorities for shared channels");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        httpClient.Respond("api/station/altnation", BuildFeedJson(("play1", "track1", "Artist One", "I'm Open", new[] { ("deezer", "https://www.deezer.com/track/624510") })));
        httpClient.Respond("api.deezer.com/track/624510", "{\"isrc\":\"USSM19601763\"}");
        httpClient.Respond("musicbrainz.org/ws/2/isrc/USSM19601763", "{\"recordings\":[{\"id\":\"rec-1\"}]}");
        httpClient.Respond("musicbrainz.org/ws/2/recording/rec-1",
            "{\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"releases\":[" +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"single-mbid\",\"title\":\"The Single\",\"primary-type\":\"Single\",\"first-release-date\":\"1996-06-01\"}}," +
            "{\"status\":\"Official\",\"artist-credit\":[{\"artist\":{\"id\":\"artist-mbid-1\",\"name\":\"Artist One\"}}]," +
            "\"release-group\":{\"id\":\"album-mbid\",\"title\":\"The Album\",\"primary-type\":\"Album\",\"first-release-date\":\"1996-08-14\"}}]}");

        var factory = new FakeImportListFactory();
        factory.AddChannel("altnation", metadataProfileId: 42, releasePriority: ReleasePriorityMode.Singles);
        factory.AddChannel("altnation", metadataProfileId: 42, releasePriority: ReleasePriorityMode.Albums);

        var profile = new MetadataProfile
        {
            Id = 42,
            PrimaryAlbumTypes = new List<ProfilePrimaryAlbumTypeItem>
            {
                new() { Allowed = true, PrimaryAlbumType = PrimaryAlbumType.Single },
                new() { Allowed = true, PrimaryAlbumType = PrimaryAlbumType.Album }
            },
            SecondaryAlbumTypes = new List<ProfileSecondaryAlbumTypeItem>
            {
                new() { Allowed = true, SecondaryAlbumType = SecondaryAlbumType.Studio }
            },
            ReleaseStatuses = new List<ProfileReleaseStatusItem>
            {
                new() { Allowed = true, ReleaseStatus = ReleaseStatus.Official }
            }
        };

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(profile), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        var store = new SXMPlaylistHistoryStore(folder);
        var singles = store.GetPresentableTracks("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.PresentationWindow, 10, releasePriority: ReleasePriorityMode.Singles);
        var albums = store.GetPresentableTracks("altnation", DateTime.UtcNow - SXMPlaylistHistoryStore.PresentationWindow, 10, releasePriority: ReleasePriorityMode.Albums);

        Assert("singles-priority presentation uses the single", singles.Count == 1 && singles[0].Album == "The Single");
        Assert("albums-priority presentation uses the album", albums.Count == 1 && albums[0].Album == "The Album");
        Assert("dual-priority resolution fetched feed, EPG, Deezer, ISRC, and recording once each",
            httpClient.RequestUrls.Count(u => u.Contains("api/station/altnation")) == 1 &&
            httpClient.RequestUrls.Count(u => u.Contains("sxmepg")) == 1 &&
            httpClient.RequestUrls.Count(u => u.Contains("api.deezer.com/track/624510")) == 1 &&
            httpClient.RequestUrls.Count(u => u.Contains("musicbrainz.org/ws/2/isrc/USSM19601763")) == 1 &&
            httpClient.RequestUrls.Count(u => u.Contains("musicbrainz.org/ws/2/recording/rec-1")) == 1);
    }

    private static void TestWorkerIdlesWithNoChannels()
    {
        Console.WriteLine("\n[Test] Worker does nothing when no XM Playlist channels are configured");

        SXMPlaylistFeedCache.Clear();
        var folder = NewFolder();
        var httpClient = new FakeHttpClient();
        var factory = new FakeImportListFactory();

        var worker = new SXMPlaylistWorker(httpClient, folder, factory, new FakeMetadataProfileService(), new FakeNotificationFactory(), LogManager.GetLogger("Test"));
        worker.RunOnce(CancellationToken.None);

        Assert("no HTTP requests made", httpClient.CallCount == 0);
        var store = new SXMPlaylistHistoryStore(folder);
        Assert("no plays recorded", store.GetPlays("altnation", DateTime.MinValue).Count == 0);
    }

    private static void TestPlexExactTitleMatch()
    {
        Console.WriteLine("\n[Test] Plex match uses exact title + artist");

        var httpClient = NewPlexEnvironment();
        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("exact match adds the Plex track", HasPlaylistItem(httpClient, "100"));
    }

    private static void TestPlexSyncReturnsTrackMatchAudit()
    {
        Console.WriteLine("\n[Test] Plex sync returns playlist track match audit details");

        var httpClient = NewPlexEnvironment();
        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[]
        {
            new PlayEventRecord(1, "p1", "altnation", "track1", "Kool & The Gang", "Celebration", DateTime.UtcNow, null, null, null, null, "recording-mbid-1", "USDE12345678")
        });

        var match = result.TrackMatches.SingleOrDefault();
        Assert("one audit match returned", match != null);
        Assert("audit carries SXM trace identifiers", match!.SxmTrackId == "track1" && match.RecordingMusicBrainzId == "recording-mbid-1" && match.Isrc == "USDE12345678");
        Assert("audit carries Plex match details", match.PlexRatingKey == "100" && match.PlexArtist == "Kool & The Gang" && match.PlexTitle == "Celebration");
        Assert("audit records match confidence", match.MatchMethod == "exact-title-artist" && match.Confidence == "high");
    }

    private static void TestPlexFeatCreditStrippedFromArtist()
    {
        Console.WriteLine("\n[Test] Plex match strips feat credits from the feed artist");

        var httpClient = NewPlexEnvironment(trackTitle: "Celebration", trackArtist: "Kool & The Gang");
        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang (feat. Foo)", "Celebration") });

        Assert("feat-stripped artist matched the Plex track", HasPlaylistItem(httpClient, "100"));
    }

    private static void TestPlexFuzzyTitleVersionMatch()
    {
        Console.WriteLine("\n[Test] Plex match falls back to fuzzy title for version suffixes");

        var httpClient = NewPlexEnvironment(trackTitle: "Celebration", trackArtist: "Kool & The Gang");
        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration (Live)") });

        Assert("version-suffixed title matched via fuzzy tier", HasPlaylistItem(httpClient, "100"));
    }

    private static void TestPlexRetriesSearchWithStrippedTitleSuffix()
    {
        Console.WriteLine("\n[Test] Plex search retries with stripped title suffixes");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("/identity", "{\"MediaContainer\":{\"machineIdentifier\":\"mach1\"}}");
        httpClient.Respond("/library/sections", "{\"MediaContainer\":{\"Directory\":[{\"key\":\"1\",\"type\":\"artist\"}]}}");
        httpClient.Respond("artist=Prince&title=When%20Doves%20Cry&", TrackMatchJson("When Doves Cry", "Prince"));
        httpClient.Respond("/playlists?playlistType=audio", "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"SXM Alt Nation\",\"ratingKey\":\"500\"}]}}");
        httpClient.Respond("/playlists/500/items", "{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"999\"}]}}");

        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Prince", "When Doves Cry (84)") });

        Assert("year-suffixed title matched after stripped-title search", HasPlaylistItem(httpClient, "100"));
        Assert("raw title was searched first", httpClient.RequestUrls.Any(u => u.Contains("title=When%20Doves%20Cry%20%2884%29")));
        Assert("stripped title was searched after raw title", httpClient.RequestUrls.Any(u => u.Contains("title=When%20Doves%20Cry&")));
    }

    private static void TestPlexMultiArtistPlayMatchesPrimaryArtist()
    {
        Console.WriteLine("\n[Test] Plex match tries each credited artist and adds one playlist entry");

        var httpClient = NewPlexEnvironment(trackTitle: "Walk This Way", trackArtist: "Run-DMC");
        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[]
        {
            PlayEvent("p1", "Run-DMC", "Walk This Way"),
            PlayEvent("p1", "Aerosmith", "Walk This Way")
        });

        Assert("primary credited artist matched the Plex track", HasPlaylistItem(httpClient, "100"));
        Assert("duet produced exactly one playlist entry", CountPlaylistItem(httpClient) == 1);
    }

    private static void TestPlexRepeatedTrackUsesPersistedCache()
    {
        Console.WriteLine("\n[Test] Plex reuses the persisted (artist, title) cache and skips re-searching");

        var httpClient = NewPlexEnvironment(trackTitle: "Celebration", trackArtist: "Kool & The Gang");
        var client = NewPlexClient(httpClient);
        client.SeedTrackCache(new Dictionary<string, PlexTrackCacheRecord>
        {
            ["Kool & The Gang||Celebration"] = new("100", "Kool & The Gang", "Celebration", "Celebrate!", "plex://track/100", "exact-title-artist", "high")
        });
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("no library search request when the pair is cached", !httpClient.RequestUrls.Any(u => u.Contains("/library/sections/1/all")));
        Assert("cached rating key exported for persistence", client.ExportTrackCache()["Kool & The Gang||Celebration"].RatingKey == "100");
        Assert("cached Plex metadata exported for audit", client.ExportTrackCache()["Kool & The Gang||Celebration"].Guid == "plex://track/100");
    }

    private static void TestPlexRetriesTransientSearchFailure()
    {
        Console.WriteLine("\n[Test] Plex retries a transient search failure");

        var httpClient = NewPlexEnvironment();
        httpClient.RespondSequence("/library/sections/1/all",
            (HttpStatusCode.InternalServerError, "{}"),
            (HttpStatusCode.OK, TrackMatchJson("Celebration", "Kool & The Gang")));
        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("transient 500 was retried and the track was added", HasPlaylistItem(httpClient, "100"));
    }

    private static void TestPlexCacheIsolationBetweenLists()
    {
        Console.WriteLine("\n[Test] Plex cache is isolated between list syncs");

        var httpClient = new FakeHttpClient();
        httpClient.Respond("/identity", "{\"MediaContainer\":{\"machineIdentifier\":\"mach1\"}}");
        httpClient.Respond("/library/sections", "{\"MediaContainer\":{\"Directory\":[{\"key\":\"1\",\"type\":\"artist\"}]}}");
        
        // Track A: Celebration by Kool & The Gang -> ratingKey 100
        httpClient.Respond("artist=Kool%20%26%20The%20Gang&title=Celebration",
            "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"Celebration\",\"grandparentTitle\":\"Kool & The Gang\",\"ratingKey\":\"100\"}]}}");
        
        // Track B: Stayin' Alive by Bee Gees -> ratingKey 200
        httpClient.Respond("artist=Bee%20Gees&title=Stayin%27%20Alive",
            "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"Stayin' Alive\",\"grandparentTitle\":\"Bee Gees\",\"ratingKey\":\"200\"}]}}");
        
        // Playlist 1
        httpClient.Respond("/playlists?playlistType=audio&title=List+1", "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"List 1\",\"ratingKey\":\"501\"}]}}");
        httpClient.Respond("/playlists/501/items", "{\"MediaContainer\":{\"Metadata\":[]}}");
        
        // Playlist 2
        httpClient.Respond("/playlists?playlistType=audio&title=List+2", "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"List 2\",\"ratingKey\":\"502\"}]}}");
        httpClient.Respond("/playlists/502/items", "{\"MediaContainer\":{\"Metadata\":[]}}");

        var client = NewPlexClient(httpClient);
        
        // Sync list 1 with Track A
        client.ClearTrackCache();
        client.Sync(1, "List 1", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });
        var cache1 = client.ExportTrackCache();
        
        // Sync list 2 with Track B
        client.ClearTrackCache();
        client.Sync(2, "List 2", new[] { PlayEvent("p2", "Bee Gees", "Stayin' Alive") });
        var cache2 = client.ExportTrackCache();

        Assert("cache 1 contains only Track A", cache1.Count == 1 && cache1.ContainsKey("Kool & The Gang||Celebration"));
        Assert("cache 2 contains only Track B", cache2.Count == 1 && cache2.ContainsKey("Bee Gees||Stayin' Alive"));
        Assert("cache 1 does not contain Track B", !cache1.ContainsKey("Bee Gees||Stayin' Alive"));
        Assert("cache 2 does not contain Track A", !cache2.ContainsKey("Kool & The Gang||Celebration"));
    }

    private static void TestPlexFanOutCreatesPlaylistForFullAccountHomeUser()
    {
        Console.WriteLine("\n[Test] Plex fan-out creates a playlist copy for a shared/full-account user");

        var httpClient = NewPlexEnvironment();
        // Plex.tv: the owner (admin) plus a full-account member "vsa191" with library access.
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}," +
            "{\"id\":\"user1\",\"uuid\":\"u-vsa\",\"title\":\"vsa191\",\"username\":\"vsa191\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"0\"}" +
            "]}}");
        // shared_servers (XML) exposes vsa191's direct server access token and music library share.
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"user1\" username=\"vsa191\" accessToken=\"vsa-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("owner playlist synced", result.OwnerPlaylistRatingKey == "500");
        Assert("full-account user playlist key persisted", result.UserPlaylistRatingKeys["user1"] == "500");
        Assert("no users skipped", result.SkippedUsers.Count == 0);
        Assert("owner account did not trigger a home-user switch", !httpClient.RequestUrls.Any(u => u.Contains("/home/users/u-owner/switch")));
    }

    private static void TestPlexFanOutCachesPlexTvUserDiscovery()
    {
        Console.WriteLine("\n[Test] Plex fan-out caches plex.tv user discovery for the daily window");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}," +
            "{\"id\":\"user1\",\"uuid\":\"u-vsa\",\"title\":\"vsa191\",\"username\":\"vsa191\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"0\"}" +
            "]}}");
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"user1\" username=\"vsa191\" accessToken=\"vsa-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });
        client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("owner id fetched once", httpClient.RequestUrls.Count(u => u.Contains("/api/v2/user")) == 1);
        Assert("shared servers fetched once", httpClient.RequestUrls.Count(u => u.Contains("/api/servers/mach1/shared_servers")) == 1);
        Assert("home users fetched once", httpClient.RequestUrls.Count(u => u.Contains("/api/v2/home/users")) == 1);
    }

    private static void TestPlexFanOutSkipsPinProtectedUser()
    {
        Console.WriteLine("\n[Test] Plex fan-out skips PIN-protected home users that need a switch");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        // The PIN-protected user is a home member NOT in shared_servers, so a switch would be needed.
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}," +
            "{\"id\":\"user1\",\"uuid\":\"u-vsa\",\"title\":\"vsa191\",\"username\":\"vsa191\",\"restricted\":\"0\",\"protected\":\"1\",\"admin\":\"0\"}" +
            "]}}");
        // No shared_servers entry for the PIN-protected user -> switch would be needed, so skip.
        httpClient.Respond("/api/servers/mach1/shared_servers", "<?xml version=\"1.0\"?><MediaContainer></MediaContainer>");

        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("owner playlist still synced", result.OwnerPlaylistRatingKey == "500");
        Assert("PIN-protected user skipped", result.SkippedUsers.Count == 1 && result.SkippedUsers[0].StartsWith("vsa191"));
        Assert("no switch attempted for PIN-protected user", !httpClient.RequestUrls.Any(u => u.Contains("/home/users/u-vsa/switch")));
    }

    private static void TestPlexFanOutUsesSwitchFallbackWhenNoSharedToken()
    {
        Console.WriteLine("\n[Test] Plex fan-out falls back to switch + resources when shared_servers has no token");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        // A managed (restricted) home member who is NOT in shared_servers -> switch + resources.
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}," +
            "{\"id\":\"user1\",\"uuid\":\"u-vsa\",\"title\":\"vsa191\",\"username\":\"vsa191\",\"restricted\":\"1\",\"protected\":\"0\",\"admin\":\"0\"}" +
            "]}}");
        // No shared_servers entry -> switch + resources exchange.
        httpClient.Respond("/api/servers/mach1/shared_servers", "<?xml version=\"1.0\"?><MediaContainer></MediaContainer>");
        httpClient.Respond("/api/v2/home/users/u-vsa/switch", "{\"authToken\":\"switched-plex-tv-token\"}");
        httpClient.Respond("/api/v2/resources",
            "{\"MediaContainer\":{\"Device\":[{\"clientIdentifier\":\"mach1\",\"accessToken\":\"vsa-server-token\"}]}}");

        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("switch endpoint was called for the managed user", httpClient.RequestUrls.Any(u => u.Contains("/home/users/u-vsa/switch")));
        Assert("resources exchange used the switched token", httpClient.RequestUrls.Any(u => u.Contains("/api/v2/resources")));
        Assert("playlist key persisted for the managed user", result.UserPlaylistRatingKeys["user1"] == "500");
        Assert("no users skipped", result.SkippedUsers.Count == 0);
    }

    private static void TestPlexFanOutCoversSharedUsersOutsideHome()
    {
        Console.WriteLine("\n[Test] Plex fan-out creates playlists for shared users outside the Plex Home");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        // Only the owner is a home user; the shared list has two external users.
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}" +
            "]}}");
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"ext1\" username=\"andyp396\" accessToken=\"andy-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "<SharedServer id=\"101\" userID=\"ext2\" username=\"jd9241\" accessToken=\"jd-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("external shared user 1 got a playlist copy", result.UserPlaylistRatingKeys["ext1"] == "500");
        Assert("external shared user 2 got a playlist copy", result.UserPlaylistRatingKeys["ext2"] == "500");
        Assert("no switch calls for shared users with direct tokens", !httpClient.RequestUrls.Any(u => u.Contains("/switch")));
        Assert("no users skipped", result.SkippedUsers.Count == 0);
    }

    private static void TestPlexFanOutSkipsUsersWithoutMusicShare()
    {
        Console.WriteLine("\n[Test] Plex fan-out skips users without the music library shared");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        httpClient.Respond("/api/v2/home/users",
            "{\"MediaContainer\":{\"User\":[" +
            "{\"id\":\"owner1\",\"uuid\":\"u-owner\",\"title\":\"owner\",\"username\":\"owner\",\"restricted\":\"0\",\"protected\":\"0\",\"admin\":\"1\"}" +
            "]}}");
        // ext1 has music shared; ext2 has only movies (no Music section).
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"ext1\" username=\"andyp396\" accessToken=\"andy-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "<SharedServer id=\"101\" userID=\"ext2\" username=\"jd9241\" accessToken=\"jd-server-token\">" +
            "<Section id=\"7\" key=\"7\" title=\"Movies\" type=\"movie\" shared=\"1\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("music-shared user got a playlist copy", result.UserPlaylistRatingKeys.ContainsKey("ext1"));
        Assert("user without music share got no playlist copy", !result.UserPlaylistRatingKeys.ContainsKey("ext2"));
        Assert("user without music share not surfaced as skipped", !result.SkippedUsers.Any(s => s.StartsWith("jd9241")));
    }

    private static void TestPlexFanOutOwnerSyncSurvivesHomeUsersFailure()
    {
        Console.WriteLine("\n[Test] Plex fan-out owner sync survives a plex.tv home-users failure");

        var httpClient = NewPlexEnvironment();
        // plex.tv endpoints return 404 (no Respond registered) -> fan-out is skipped.
        var client = NewPlexClient(httpClient);
        var result = client.Sync(42, "SXM Alt Nation", new[] { PlayEvent("p1", "Kool & The Gang", "Celebration") });

        Assert("owner playlist still synced when plex.tv is unreachable", result.OwnerPlaylistRatingKey == "500");
        Assert("no skipped users surfaced on enumeration failure", result.SkippedUsers.Count == 0);
    }

    private static void TestPlexCleanupDeletesOwnerAndUserCopies()
    {
        Console.WriteLine("\n[Test] Plex cleanup deletes the owner and fan-out copies when a list is removed");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"user1\" username=\"vsa191\" accessToken=\"vsa-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"1\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        var completed = client.CleanupPlaylist(
            "SXM Alt Nation",
            "500",
            new Dictionary<string, string> { ["user1"] = "600" });

        Assert("cleanup reported complete", completed);
        Assert("owner playlist delete attempted", httpClient.RequestUrls.Any(u => u.Contains("/playlists/500")));
        Assert("user copy delete attempted with the user's token", httpClient.RequestUrls.Any(u => u.Contains("/playlists/600")));
    }

    private static void TestPlexCleanupDefersWhenPlexTvUnreachable()
    {
        Console.WriteLine("\n[Test] Plex cleanup reports incomplete when plex.tv is unreachable");

        var httpClient = NewPlexEnvironment();
        // No shared_servers Respond -> plex.tv returns 404 -> token lookup returns null.
        var client = NewPlexClient(httpClient);
        var completed = client.CleanupPlaylist(
            "SXM Alt Nation",
            "500",
            new Dictionary<string, string> { ["user1"] = "600" });

        Assert("cleanup reported incomplete (defer state deletion)", !completed);
    }

    private static void TestPlexPruneRemovesUnsharedUserCopies()
    {
        Console.WriteLine("\n[Test] Plex prune removes copies for users who lost the music library share");

        var httpClient = NewPlexEnvironment();
        httpClient.Respond("/api/v2/user", "{\"id\":\"owner1\",\"username\":\"owner\"}");
        httpClient.Respond("/api/servers/mach1/shared_servers",
            "<?xml version=\"1.0\"?><MediaContainer>" +
            "<SharedServer id=\"100\" userID=\"user1\" username=\"vsa191\" accessToken=\"vsa-server-token\">" +
            "<Section id=\"19\" key=\"19\" title=\"Music\" type=\"artist\" shared=\"0\"/>" +
            "</SharedServer>" +
            "</MediaContainer>");

        var client = NewPlexClient(httpClient);
        var retained = client.PruneUnsharedPlaylistCopies(
            "SXM Alt Nation",
            new Dictionary<string, string> { ["user1"] = "600", ["user2"] = "601" });

        Assert("music-unshared user's copy deleted", httpClient.RequestUrls.Any(u => u.Contains("/playlists/600")));
        Assert("user2 (absent from shared_servers) key dropped", !retained.ContainsKey("user2"));
        Assert("no keys retained for unshared users", retained.Count == 0);
    }

    private static void TestPlexPruneKeepsAllWhenPlexTvUnreachable()
    {
        Console.WriteLine("\n[Test] Plex prune keeps all keys when plex.tv is unreachable");

        var httpClient = NewPlexEnvironment();
        // No shared_servers Respond -> plex.tv 404 -> lookup returns null -> keep everything.
        var client = NewPlexClient(httpClient);
        var retained = client.PruneUnsharedPlaylistCopies(
            "SXM Alt Nation",
            new Dictionary<string, string> { ["user1"] = "600" });

        Assert("all keys retained when plex.tv unreachable", retained.ContainsKey("user1") && retained["user1"] == "600");
    }

    private static PlayEventRecord PlayEvent(string playId, string artist, string song, long eventId = 1)
    {
        return new PlayEventRecord(eventId, playId, "altnation", null, artist, song, DateTime.UtcNow, null, null, null, null);
    }

    private static SXMPlaylistPlexClient NewPlexClient(FakeHttpClient httpClient)
    {
        var factory = new FakeNotificationFactory();
        factory.AddPlexServer();
        return new SXMPlaylistPlexClient(httpClient, factory, LogManager.GetLogger("Test"));
    }

    private static FakeHttpClient NewPlexEnvironment(string trackTitle = "Celebration", string trackArtist = "Kool & The Gang")
    {
        var httpClient = new FakeHttpClient();
        httpClient.Respond("/identity", "{\"MediaContainer\":{\"machineIdentifier\":\"mach1\"}}");
        httpClient.Respond("/library/sections", "{\"MediaContainer\":{\"Directory\":[{\"key\":\"1\",\"type\":\"artist\"}]}}");
        httpClient.Respond("/library/sections/1/all", TrackMatchJson(trackTitle, trackArtist));
        httpClient.Respond("/playlists?playlistType=audio", "{\"MediaContainer\":{\"Metadata\":[{\"title\":\"SXM Alt Nation\",\"ratingKey\":\"500\"}]}}");
        httpClient.Respond("/playlists/500/items", "{\"MediaContainer\":{\"Metadata\":[{\"ratingKey\":\"999\"}]}}");
        return httpClient;
    }

    private static string TrackMatchJson(string title, string artist)
    {
        return $"{{\"MediaContainer\":{{\"Metadata\":[{{\"title\":\"{title}\",\"grandparentTitle\":\"{artist}\",\"ratingKey\":\"100\"}}]}}}}";
    }

    private static bool HasPlaylistItem(FakeHttpClient httpClient, string ratingKey)
    {
        return httpClient.RequestUrls.Any(u => u.Contains("/playlists/500/items?uri=") && u.Contains(ratingKey));
    }

    private static int CountPlaylistItem(FakeHttpClient httpClient)
    {
        return httpClient.RequestUrls.Count(u => u.Contains("/playlists/500/items?uri="));
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

    private static SXMPlaylistHistoryStore NewHistoryStore()
    {
        return new SXMPlaylistHistoryStore(NewFolder());
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
    public string LastRequestUrl { get; private set; } = "";
    public IReadOnlyList<string> RequestUrls => _requestUrls;

    private readonly List<string> _requestUrls = new();

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
        LastRequestUrl = request.Url.FullUri;
        _requestUrls.Add(LastRequestUrl);

        // Match the longest fragment first so a specific endpoint (e.g. "/library/sections/1/all")
        // wins over a prefix that also matches it ("/library/sections").
        var sequenceMatch = _sequencesByUrlFragment
            .Where(p => p.Value.Count > 0 && request.Url.FullUri.Contains(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(sequenceMatch.Key))
        {
            var (status, content) = sequenceMatch.Value.Dequeue();
            return new HttpResponse(request, new HttpHeader(), content, status);
        }

        var responseMatch = _responsesByUrlFragment
            .Where(p => request.Url.FullUri.Contains(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .FirstOrDefault();
        if (!string.IsNullOrEmpty(responseMatch.Key))
        {
            return new HttpResponse(request, new HttpHeader(), responseMatch.Value, HttpStatusCode.OK);
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
    private readonly Dictionary<int, List<Album>> _withFilesByArtistId = new();

    public void Add(Album album)
    {
        _byForeignId[album.ForeignAlbumId] = album;
    }

    public void AddWithFiles(Album album, Artist artist)
    {
        if (!_withFilesByArtistId.TryGetValue(artist.Id, out var albums))
        {
            albums = new List<Album>();
            _withFilesByArtistId[artist.Id] = albums;
        }

        albums.Add(album);
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
    public List<Album> GetArtistAlbumsWithFiles(Artist artist) => _withFilesByArtistId.TryGetValue(artist.Id, out var albums) ? albums : new List<Album>();
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

    public void AddChannel(string channel, int metadataProfileId = 0, ReleasePriorityMode releasePriority = ReleasePriorityMode.Singles)
    {
        _definitions.Add(new ImportListDefinition
        {
            Implementation = "SXMPlaylistImport",
            EnableAutomaticAdd = true,
            MetadataProfileId = metadataProfileId,
            Settings = new SXMPlaylistImportSettings { Channel = channel, ReleasePriority = releasePriority }
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

internal class FakeImportListRepository : IImportListRepository
{
    private readonly List<ImportListDefinition> _definitions = new();

    public void Add(int id, string channel, string show)
    {
        _definitions.Add(new ImportListDefinition
        {
            Id = id,
            Implementation = nameof(SXMPlaylistImport),
            Settings = new SXMPlaylistImportSettings { Channel = channel, Show = show }
        });
    }

    public IEnumerable<ImportListDefinition> All() => _definitions;
    public int Count() => _definitions.Count;
    public ImportListDefinition Find(int id) => _definitions.FirstOrDefault(d => d.Id == id)!;
    public ImportListDefinition Get(int id) => _definitions.Single(d => d.Id == id);
    public IEnumerable<ImportListDefinition> Get(IEnumerable<int> ids) => _definitions.Where(d => ids.Contains(d.Id));
    public bool HasItems() => _definitions.Count > 0;
    public ImportListDefinition Single() => _definitions.Single();
    public ImportListDefinition SingleOrDefault() => _definitions.SingleOrDefault()!;
    public void UpdateSettings(ImportListDefinition model) => throw new NotSupportedException();
    public ImportListDefinition Insert(ImportListDefinition model) => throw new NotSupportedException();
    public ImportListDefinition Update(ImportListDefinition model) => throw new NotSupportedException();
    public ImportListDefinition Upsert(ImportListDefinition model) => throw new NotSupportedException();
    public void SetFields(ImportListDefinition model, params Expression<Func<ImportListDefinition, object>>[] properties) => throw new NotSupportedException();
    public void Delete(ImportListDefinition model) => throw new NotSupportedException();
    public void Delete(int id) => throw new NotSupportedException();
    public void InsertMany(IList<ImportListDefinition> model) => throw new NotSupportedException();
    public void UpdateMany(IList<ImportListDefinition> model) => throw new NotSupportedException();
    public void SetFields(IList<ImportListDefinition> models, params Expression<Func<ImportListDefinition, object>>[] properties) => throw new NotSupportedException();
    public void DeleteMany(List<ImportListDefinition> model) => throw new NotSupportedException();
    public void DeleteMany(IEnumerable<int> ids) => throw new NotSupportedException();
    public void Purge(bool vacuum = false) => throw new NotSupportedException();
    public PagingSpec<ImportListDefinition> GetPaged(PagingSpec<ImportListDefinition> pagingSpec) => throw new NotSupportedException();
}

internal class FakeMetadataProfileService : IMetadataProfileService
{
    private readonly Dictionary<int, MetadataProfile> _profiles = new();
    private MetadataProfile? _defaultProfile;

    public FakeMetadataProfileService()
    {
    }

    public FakeMetadataProfileService(params MetadataProfile[] profiles)
    {
        foreach (var profile in profiles)
        {
            _profiles[profile.Id] = profile;
        }
    }

    public void SetDefault(MetadataProfile profile) => _defaultProfile = profile;

    public MetadataProfile Add(MetadataProfile profile)
    {
        _profiles[profile.Id] = profile;
        return profile;
    }

    public void Update(MetadataProfile profile) => _profiles[profile.Id] = profile;
    public void Delete(int id) => _profiles.Remove(id);
    public List<MetadataProfile> All() => _profiles.Values.ToList();
    public MetadataProfile Get(int id) => _profiles.TryGetValue(id, out var p) ? p : _defaultProfile ?? throw new NotSupportedException();
    public bool Exists(int id) => _profiles.ContainsKey(id);
}

internal class FakeNotificationFactory : INotificationFactory
{
    private readonly List<INotification> _providers = new();

    public FakeNotificationFactory()
    {
    }

    public FakeNotificationFactory(params INotification[] providers)
    {
        _providers.AddRange(providers);
    }

    public void AddPlexServer(string host = "127.0.0.1", int port = 32400, bool useSsl = false, string? urlBase = null, string? authToken = "token123")
    {
        var definition = new NotificationDefinition
        {
            Implementation = nameof(PlexServer),
            Settings = new PlexServerSettings
            {
                Host = host,
                Port = port,
                UseSsl = useSsl,
                UrlBase = urlBase,
                AuthToken = authToken
            }
        };

        _providers.Add(new FakePlexNotification(definition));
    }

    public List<INotification> GetAvailableProviders() => _providers;
    public List<NotificationDefinition> All() => _providers.Select(p => (NotificationDefinition)p.Definition).ToList();

    public List<INotification> OnGrabEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnReleaseImportEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnUpgradeEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnRenameEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnArtistAddEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnArtistDeleteEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnAlbumDeleteEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnHealthIssueEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnHealthRestoredEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnDownloadFailureEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnImportFailureEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnTrackRetagEnabled(bool filterBlockedNotifications = true) => new();
    public List<INotification> OnApplicationUpdateEnabled(bool filterBlockedNotifications = true) => new();
    public bool Exists(int id) => throw new NotSupportedException();
    public NotificationDefinition Find(int id) => throw new NotSupportedException();
    public NotificationDefinition Get(int id) => throw new NotSupportedException();
    public IEnumerable<NotificationDefinition> Get(IEnumerable<int> ids) => throw new NotSupportedException();
    public NotificationDefinition Create(NotificationDefinition definition) => throw new NotSupportedException();
    public void Update(NotificationDefinition definition) => throw new NotSupportedException();
    public IEnumerable<NotificationDefinition> Update(IEnumerable<NotificationDefinition> definitions) => throw new NotSupportedException();
    public void Delete(int id) => throw new NotSupportedException();
    public void Delete(IEnumerable<int> ids) => throw new NotSupportedException();
    public IEnumerable<NotificationDefinition> GetDefaultDefinitions() => throw new NotSupportedException();
    public IEnumerable<NotificationDefinition> GetPresetDefinitions(NotificationDefinition providerDefinition) => throw new NotSupportedException();
    public void SetProviderCharacteristics(NotificationDefinition definition) => throw new NotSupportedException();
    public void SetProviderCharacteristics(INotification provider, NotificationDefinition definition) => throw new NotSupportedException();
    public INotification GetInstance(NotificationDefinition definition) => throw new NotSupportedException();
    public ValidationResult Test(NotificationDefinition definition) => throw new NotSupportedException();
    public object RequestAction(NotificationDefinition definition, string action, IDictionary<string, string> query) => throw new NotSupportedException();
    public List<NotificationDefinition> AllForTag(int tagId) => throw new NotSupportedException();
}

internal class FakePlexNotification : INotification
{
    public FakePlexNotification(NotificationDefinition definition)
    {
        Definition = definition;
    }

    public string Name => "Plex Media Server";
    public string Link => "https://www.plex.tv/";
    public Type ConfigContract => typeof(PlexServerSettings);
    public ProviderMessage Message => null!;
    public IEnumerable<ProviderDefinition> DefaultDefinitions => Array.Empty<ProviderDefinition>();
    public ProviderDefinition Definition { get; set; }
    public bool SupportsOnGrab => false;
    public bool SupportsOnReleaseImport => false;
    public bool SupportsOnUpgrade => false;
    public bool SupportsOnRename => false;
    public bool SupportsOnArtistAdd => false;
    public bool SupportsOnArtistDelete => false;
    public bool SupportsOnAlbumDelete => false;
    public bool SupportsOnHealthIssue => false;
    public bool SupportsOnHealthRestored => false;
    public bool SupportsOnApplicationUpdate => false;
    public bool SupportsOnDownloadFailure => false;
    public bool SupportsOnImportFailure => false;
    public bool SupportsOnTrackRetag => false;
    public void OnGrab(GrabMessage grabMessage) { }
    public void OnReleaseImport(AlbumDownloadMessage message) { }
    public void OnRename(Artist artist, List<RenamedTrackFile> renamedFiles) { }
    public void OnArtistAdd(ArtistAddMessage message) { }
    public void OnArtistDelete(ArtistDeleteMessage deleteMessage) { }
    public void OnAlbumDelete(AlbumDeleteMessage deleteMessage) { }
    public void OnHealthIssue(NzbDrone.Core.HealthCheck.HealthCheck healthCheck) { }
    public void OnHealthRestored(NzbDrone.Core.HealthCheck.HealthCheck previousCheck) { }
    public void OnApplicationUpdate(ApplicationUpdateMessage updateMessage) { }
    public void OnDownloadFailure(DownloadFailedMessage message) { }
    public void OnImportFailure(AlbumDownloadMessage message) { }
    public void OnTrackRetag(TrackRetagMessage message) { }
    public void ProcessQueue() { }
    public ValidationResult Test() => new();
    public object RequestAction(string action, IDictionary<string, string> query) => new();
}
