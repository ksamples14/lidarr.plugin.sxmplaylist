using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace XmPlaylist.ImportLists
{
    // xmplaylist only gives a song title, never a real album name. Every play's `links` block
    // includes per-service URLs for that same track, some of which point at real, free, unauthenticated
    // catalog data we can use to find the actual album:
    //
    //   1. Deezer link -> Deezer's public API, which returns both an ISRC and Deezer's own album
    //      title in the same call:
    //        1a. ISRC -> MusicBrainz's exact ISRC lookup -> a real MusicBrainz release-group. This is
    //            the precise path: an ISRC identifies one specific recording, not a fuzzy text match,
    //            so we can hand Lidarr real MusicBrainz IDs directly.
    //        1b. If that MusicBrainz path doesn't pan out (no ISRC, no MB match, no release-group),
    //            fall back to Deezer's own album title - we already paid for that API call, no reason
    //            to throw its answer away and spend a second call on Apple before trying it.
    //   2. Apple Music link -> iTunes Lookup API -> a real album title (no MusicBrainz ID). Used only
    //      when there's no Deezer link at all, or Deezer's track has no album title either.
    //
    // Results are cached by the caller (XmPlaylistHistoryStore, keyed by track id) since the same
    // song replays constantly on a rotation-heavy station and its album never changes between plays.
    public class XmPlaylistAlbumResolver
    {
        private static readonly Regex DeezerTrackId = new(@"deezer\.com/track/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AppleAlbumId = new(@"/album/[^/]+/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // MusicBrainz's entity for the "Various Artists" pseudo-artist that every compilation is
        // credited to. Compilations are useless for our purpose - a played track should resolve to
        // the artist's own release, not whichever "Summer Hits" compilation it also appears on.
        private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

        private static readonly TimeSpan MusicBrainzMinInterval = TimeSpan.FromSeconds(1.1);
        private static readonly int MusicBrainzMaxRetries = 2;
        private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
        private static DateTime _lastMusicBrainzCallUtc = DateTime.MinValue;

        // Only touched when a retry fires; small enough to keep tests fast.
        internal static TimeSpan MusicBrainzRetryBackoff = TimeSpan.FromSeconds(2);

        private const string UserAgent = "XmPlaylist-Lidarr-Plugin/1.0 (https://github.com/ksamples14/lidarr.plugin.xmplaylist)";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public XmPlaylistAlbumResolver(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public AlbumResolution Resolve(string artist, string song, IReadOnlyDictionary<string, string> links)
        {
            try
            {
                var viaDeezer = ResolveViaDeezerAndMusicBrainz(links);
                if (viaDeezer != null)
                {
                    return viaDeezer;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Deezer/MusicBrainz album lookup failed for {0} - {1}", artist, song);
            }

            try
            {
                var viaApple = ResolveViaAppleMusic(links);
                if (viaApple != null)
                {
                    return viaApple;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Apple Music album lookup failed for {0} - {1}", artist, song);
            }

            return AlbumResolution.NotFound;
        }

        private AlbumResolution? ResolveViaDeezerAndMusicBrainz(IReadOnlyDictionary<string, string> links)
        {
            if (!links.TryGetValue("deezer", out var deezerUrl))
            {
                return null;
            }

            var match = DeezerTrackId.Match(deezerUrl);
            if (!match.Success)
            {
                return null;
            }

            var track = GetJson($"https://api.deezer.com/track/{match.Groups[1].Value}");
            var deezerAlbumTitle = track?["album"]?["title"]?.Value<string>();

            var viaMusicBrainz = ResolveViaMusicBrainz(track);
            if (viaMusicBrainz != null)
            {
                return viaMusicBrainz;
            }

            // MusicBrainz didn't pan out - Deezer's own title is still a real album name, just
            // without a MusicBrainz ID attached (Lidarr's own fuzzy search resolves it from here).
            return deezerAlbumTitle.IsNotNullOrWhiteSpace() ? new AlbumResolution(true, deezerAlbumTitle, null, null) : null;
        }

        private AlbumResolution? ResolveViaMusicBrainz(JToken? track)
        {
            var isrc = track?["isrc"]?.Value<string>();
            if (isrc.IsNullOrWhiteSpace())
            {
                return null;
            }

            ThrottleMusicBrainz();
            var isrcResult = GetJson($"https://musicbrainz.org/ws/2/isrc/{isrc}?fmt=json", musicBrainz: true);
            var recordingId = isrcResult?["recordings"]?.FirstOrDefault()?["id"]?.Value<string>();

            if (recordingId.IsNullOrWhiteSpace())
            {
                return null;
            }

            ThrottleMusicBrainz();
            var recording = GetJson(
                $"https://musicbrainz.org/ws/2/recording/{recordingId}?inc=releases+release-groups+artist-credits&fmt=json",
                musicBrainz: true);

            var artistCredits = recording?["artist-credit"] as JArray;
            string? artistMbid = artistCredits is { Count: 1 } ? artistCredits[0]["artist"]?["id"]?.Value<string>() : null;

            var releaseGroup = SelectBestReleaseGroup(recording, artistMbid);
            if (releaseGroup == null)
            {
                return null;
            }

            var albumTitle = releaseGroup["title"]?.Value<string>();
            var albumMbid = releaseGroup["id"]?.Value<string>();

            if (albumTitle.IsNullOrWhiteSpace() || albumMbid.IsNullOrWhiteSpace())
            {
                return null;
            }

            return new AlbumResolution(true, albumTitle, artistMbid, albumMbid);
        }

        private static JToken? SelectBestReleaseGroup(JToken? recording, string? recordingArtistId)
        {
            var releases = recording?["releases"] as JArray;
            if (releases == null || releases.Count == 0)
            {
                return null;
            }

            // Drop compilations outright - a compilation's release-group belongs to Various Artists,
            // and handing Lidarr that album just attaches the play to VA instead of the real artist.
            var candidates = releases
                .Where(r => r["release-group"] != null)
                .Where(r => !IsVariousArtistsRelease(r))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates
                .OrderByDescending(r => recordingArtistId.IsNotNullOrWhiteSpace() && MatchesRecordingArtist(r, recordingArtistId))
                .ThenByDescending(r => string.Equals(r["status"]?.Value<string>(), "Official", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => PrimaryTypeRank(r["release-group"]))
                .ThenBy(r => r["release-group"]?["first-release-date"]?.Value<string>() ?? "9999")
                .Select(r => r["release-group"])
                .FirstOrDefault();
        }

        // For a played track, the release that most directly corresponds to it is the single, then
        // the EP, then the studio album - so rank primary types in that order.
        private static int PrimaryTypeRank(JToken? releaseGroup)
        {
            return (releaseGroup?["primary-type"]?.Value<string>()) switch
            {
                "Single" => 2,
                "EP" => 1,
                "Album" => 0,
                _ => -1
            };
        }

        private static bool IsVariousArtistsRelease(JToken release)
        {
            var artist = FirstCreditedArtist(release);
            if (artist == null)
            {
                return false;
            }

            var name = artist["name"]?.Value<string>();
            var id = artist["id"]?.Value<string>();

            return string.Equals(name, "Various Artists", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, VariousArtistsMbid, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesRecordingArtist(JToken release, string recordingArtistId)
        {
            var artist = FirstCreditedArtist(release);
            return artist != null
                && string.Equals(artist["id"]?.Value<string>(), recordingArtistId, StringComparison.OrdinalIgnoreCase);
        }

        private static JToken? FirstCreditedArtist(JToken release)
        {
            var credit = release["artist-credit"] as JArray;
            if (credit == null || credit.Count == 0)
            {
                // Some responses only carry artist info on the release-group itself.
                credit = release["release-group"]?["artist-credit"] as JArray;
            }

            if (credit == null || credit.Count == 0)
            {
                return null;
            }

            return credit[0]["artist"];
        }

        private AlbumResolution? ResolveViaAppleMusic(IReadOnlyDictionary<string, string> links)
        {
            if (!links.TryGetValue("appleMusic", out var appleUrl))
            {
                return null;
            }

            var match = AppleAlbumId.Match(appleUrl);
            if (!match.Success)
            {
                return null;
            }

            var lookup = GetJson($"https://itunes.apple.com/lookup?id={match.Groups[1].Value}");
            var albumTitle = lookup?["results"]?.FirstOrDefault()?["collectionName"]?.Value<string>();

            return albumTitle.IsNotNullOrWhiteSpace() ? new AlbumResolution(true, albumTitle, null, null) : null;
        }

        private JToken? GetJson(string url, bool musicBrainz = false)
        {
            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add("User-Agent", UserAgent);

            for (var attempt = 0; ; attempt++)
            {
                var response = _httpClient.Get(request);

                if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content.IsNotNullOrWhiteSpace())
                {
                    return JToken.Parse(response.Content);
                }

                // MusicBrainz's 503 "server busy" responses are transient (rate/load throttling).
                // Retry a couple of times with a short backoff rather than falling through to a
                // fuzzier source; the retries stay within the 1 req/s pacing already enforced.
                var retriable = musicBrainz
                    && attempt < MusicBrainzMaxRetries
                    && (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                        || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests);

                if (!retriable)
                {
                    return null;
                }

                var backoff = MusicBrainzRetryBackoff * (attempt + 1);
                _logger.Debug("MusicBrainz returned {0}, retrying in {1}ms", response.StatusCode, backoff.TotalMilliseconds);
                Thread.Sleep(backoff);
            }
        }

        private static void ThrottleMusicBrainz()
        {
            MusicBrainzGate.Wait();
            try
            {
                var elapsed = DateTime.UtcNow - _lastMusicBrainzCallUtc;
                if (elapsed < MusicBrainzMinInterval)
                {
                    Thread.Sleep(MusicBrainzMinInterval - elapsed);
                }

                _lastMusicBrainzCallUtc = DateTime.UtcNow;
            }
            finally
            {
                MusicBrainzGate.Release();
            }
        }
    }
}
