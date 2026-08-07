using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// xmplaylist only gives a song title, never a real album name. Every play's `links` block
    /// includes per-service URLs for that same track, some of which point at real, free, unauthenticated
    /// catalog data we can use to find the actual album:
    /// 
    /// 1. Deezer link -> Deezer's public API, which returns both an ISRC and Deezer's own album
    /// title in the same call:
    /// 1a. ISRC -> MusicBrainz's exact ISRC lookup -> a real MusicBrainz release-group. This is
    /// the precise path: an ISRC identifies one specific recording, not a fuzzy text match,
    /// so we can hand Lidarr real MusicBrainz IDs directly.
    /// 1b. If that MusicBrainz path doesn't pan out (no ISRC, no MB match, no release-group),
    /// fall back to Deezer's own album title - we already paid for that API call, no reason
    /// to throw its answer away and spend a second call on Apple before trying it.
    /// 2. Apple Music link -> iTunes Lookup API -> a real album title (no MusicBrainz ID). Used only
    /// when there's no Deezer link at all, or Deezer's track has no album title either.
    /// 
    /// Results are cached by the caller (SXMPlaylistHistoryStore, keyed by track id) since the same
    /// song replays constantly on a rotation-heavy station and its album never changes between plays.
    /// </summary>
    public class SXMPlaylistAlbumResolver
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

        private const string UserAgent = "SXMPlaylist-Lidarr-Plugin/1.0 (https://github.com/ksamples14/lidarr.plugin.sxmplaylist)";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public SXMPlaylistAlbumResolver(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public AlbumResolution Resolve(string artist, string song, IReadOnlyDictionary<string, string> links, AlbumTypeFilter? filter = null)
        {
            var effectiveFilter = filter ?? AlbumTypeFilter.Unrestricted;

            try
            {
                var viaDeezer = ResolveViaDeezerAndMusicBrainz(artist, links, effectiveFilter);
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
                var viaApple = ResolveViaAppleMusic(artist, links, effectiveFilter);
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

        private AlbumResolution? ResolveViaDeezerAndMusicBrainz(string artist, IReadOnlyDictionary<string, string> links, AlbumTypeFilter filter)
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

            var viaMusicBrainz = ResolveViaMusicBrainz(track, filter);
            if (viaMusicBrainz != null)
            {
                return viaMusicBrainz;
            }

            // ISRC->MB didn't pan out - try a release title search for a real MBID before falling
            // back to Deezer's raw title.
            var viaTitleSearch = ResolveViaMusicBrainzTitleSearch(artist, deezerAlbumTitle, filter);
            if (viaTitleSearch != null)
            {
                return viaTitleSearch;
            }

            // MusicBrainz didn't pan out - Deezer's own title is still a real album name, just
            // without a MusicBrainz ID attached (Lidarr's own fuzzy search resolves it from here).
            return deezerAlbumTitle.IsNotNullOrWhiteSpace() ? new AlbumResolution(true, deezerAlbumTitle, null, null) : null;
        }

        private AlbumResolution? ResolveViaMusicBrainz(JToken? track, AlbumTypeFilter filter)
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

            var releaseGroup = SelectBestReleaseGroup(recording, artistMbid, filter);
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

        // Fallback after the exact ISRC path misses: search MusicBrainz release-groups by the
        // artist + the album title we already got (from Deezer or Apple), gated by a fuzzy
        // artist-credit match and a title-similarity threshold so a same-titled album by a
        // different artist can't be attached. Returns a real MBID when a match is confident.
        private AlbumResolution? ResolveViaMusicBrainzTitleSearch(string artist, string? albumTitle, AlbumTypeFilter filter)
        {
            if (artist.IsNullOrWhiteSpace() || albumTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var query = BuildTitleSearchQuery(artist, albumTitle!);
            ThrottleMusicBrainz();
            var result = GetJson($"https://musicbrainz.org/ws/2/release?query={Uri.EscapeDataString(query)}&fmt=json&limit=10", musicBrainz: true);

            var releases = result?["releases"] as JArray;
            if (releases == null || releases.Count == 0)
            {
                return null;
            }

            var candidates = new JArray();
            foreach (var release in releases)
            {
                var rg = release["release-group"];
                if (rg == null)
                {
                    continue;
                }

                // Artist gate: the release's credited artist must plausibly be the played artist.
                if (!ArtistCreditMatches(release, artist))
                {
                    continue;
                }

                // Title gate: release-group title must be similar enough to the Deezer/Apple title.
                var rgTitle = rg["title"]?.Value<string>();
                if (rgTitle.IsNullOrWhiteSpace() || TitleSimilarity(rgTitle!, albumTitle!) < TitleMatchThreshold)
                {
                    continue;
                }

                candidates.Add(release);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            // Reuse the approved ranking (status > primary > secondary > date, VA excluded).
            var syntheticRecording = new JObject { ["releases"] = candidates };
            var selectedReleaseGroup = SelectBestReleaseGroup(syntheticRecording, null, filter);
            if (selectedReleaseGroup == null)
            {
                return null;
            }

            var finalTitle = selectedReleaseGroup["title"]?.Value<string>();
            var finalAlbumMbid = selectedReleaseGroup["id"]?.Value<string>();

            // Artist MBID: release-search results carry artist-credit on the release, not the
            // release-group. Take the credited artist's id from the release that owns the
            // selected release-group, when a single artist is credited.
            var selectedRelease = candidates
                .Cast<JToken>()
                .FirstOrDefault(r => string.Equals(r["release-group"]?["id"]?.Value<string>(), finalAlbumMbid, StringComparison.OrdinalIgnoreCase));

            var artistCredits = selectedRelease?["artist-credit"] as JArray;
            string? artistMbid = artistCredits is { Count: 1 } ? artistCredits[0]["artist"]?["id"]?.Value<string>() : null;

            if (finalTitle.IsNullOrWhiteSpace() || finalAlbumMbid.IsNullOrWhiteSpace())
            {
                return null;
            }

            return new AlbumResolution(true, finalTitle, artistMbid, finalAlbumMbid);
        }

        // Boosted-OR form (DroppedNeedle pattern): phrase boost the title, fall back to unquoted
        // tokens, all AND'd against the artist so recall stays high while precision lives in code.
        // The edition suffix is stripped from the query too - a "Three Cheers (Deluxe Edition)"
        // phrase won't match MusicBrainz's clean "Three Cheers for Sweet Revenge" otherwise.
        private static string BuildTitleSearchQuery(string artist, string albumTitle)
        {
            var stripped = EditionSuffixPattern.Replace(albumTitle, " ").Trim();
            var title = EscapeLucene(stripped);
            var artistQuery = EscapeLucene(artist);
            return $"(releasegroup:\"{title}\"^3 OR release:\"{title}\"^2 OR {title}) AND artist:\"{artistQuery}\"";
        }

        // Escapes MusicBrainz Lucene special characters before interpolation.
        private static string EscapeLucene(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                if ("+-&&||!(){}[]^\"~*?:\\/".IndexOf(c) >= 0)
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        // True when any credited artist on the release plausibly matches the played artist name.
        private static bool ArtistCreditMatches(JToken release, string playedArtist)
        {
            var credit = release["artist-credit"] as JArray;
            if (credit == null || credit.Count == 0)
            {
                return true;
            }

            foreach (var entry in credit)
            {
                var name = entry["artist"]?["name"]?.Value<string>();
                if (name.IsNotNullOrWhiteSpace() && ArtistSimilarity(name!, playedArtist) >= ArtistMatchFloor)
                {
                    return true;
                }
            }

            return false;
        }

        private const double TitleMatchThreshold = 0.85;
        private const double ArtistMatchFloor = 0.6;

        // Edition qualifiers stripped before scoring/querying so "Three Cheers (Deluxe)" matches
        // MusicBrainz's clean "Three Cheers for Sweet Revenge".
        private static readonly Regex EditionSuffixPattern = new(
            @"[\(\[\{]\s*(deluxe|remaster(ed)?|edition|anniversary|special|expanded|bonus|complete|acoustic|live|demo|radio edit|extended|instrumental|mono|stereo|explicit|clean|version|single|promo)\b[^\)\]\}]*[\)\]\}]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string NormalizeForMatch(string value)
        {
            value = EditionSuffixPattern.Replace(value, " ");

            // Strip diacritics (Beyoncé -> Beyonce, Mötley Crüe -> Motley Crue).
            var normalized = value.Normalize(System.Text.NormalizationForm.FormD);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        sb.Append(char.ToLowerInvariant(c));
                    }
                    else
                    {
                        sb.Append(' ');
                    }
                }
            }

            return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        // Max of token-set overlap (word-order invariant, handles reordering), normalized edit
        // distance (catches near-identical strings), and token containment (handles subset titles
        // like "Best Of" vs "The Best Of", mirroring token_set_ratio's tolerance). 0..1.
        private static double TitleSimilarity(string a, string b)
        {
            var na = NormalizeForMatch(a);
            var nb = NormalizeForMatch(b);
            if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb))
            {
                return 0.0;
            }

            var tokensA = na.Split(' ').ToHashSet();
            var tokensB = nb.Split(' ').ToHashSet();
            var intersection = tokensA.Intersect(tokensB).Count();
            var union = tokensA.Union(tokensB).Count();
            var tokenScore = union == 0 ? 0.0 : (double)intersection / union;

            // Containment: the fraction of the smaller token set present in the larger, so
            // "best of" ⊂ "the best of" scores highly like token_set_ratio.
            var smaller = Math.Min(tokensA.Count, tokensB.Count);
            var containment = smaller == 0 ? 0.0 : (double)intersection / smaller;

            var editScore = 1.0 - (double)LevenshteinDistance(na, nb) / Math.Max(na.Length, nb.Length);

            return Math.Max(Math.Max(tokenScore, containment), editScore);
        }

        // Similarity of two artist names against a looser floor (display names vary more than
        // album titles). Uses containment of the shorter name in the longer as a token-set proxy.
        private static double ArtistSimilarity(string a, string b)
        {
            var na = NormalizeForMatch(a);
            var nb = NormalizeForMatch(b);
            if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb))
            {
                return 0.0;
            }

            if (na == nb)
            {
                return 1.0;
            }

            var tokensA = na.Split(' ').ToHashSet();
            var tokensB = nb.Split(' ').ToHashSet();
            var intersection = tokensA.Intersect(tokensB).Count();
            var union = tokensA.Union(tokensB).Count();
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        private static int LevenshteinDistance(string a, string b)
        {
            var dp = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++)
            {
                dp[i, 0] = i;
            }

            for (var j = 0; j <= b.Length; j++)
            {
                dp[0, j] = j;
            }

            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
                }
            }

            return dp[a.Length, b.Length];
        }

        private static JToken? SelectBestReleaseGroup(JToken? recording, string? recordingArtistId, AlbumTypeFilter filter)
        {
            var releases = recording?["releases"] as JArray;
            if (releases == null || releases.Count == 0)
            {
                return null;
            }

            // Various Artists releases are hard-excluded - a compilation's release-group belongs
            // to Various Artists (or a curator/DJ credited as the artist), and handing Lidarr that
            // album just attaches the play to the wrong artist. Compilation-typed releases are no
            // longer dropped here: they rank lowest in the secondary-type ladder and are only
            // selected when nothing higher ranks. Then filter to the types/status the list's
            // metadata profile allows.
            var candidates = releases
                .Where(r => r["release-group"] != null)
                .Where(r => !IsVariousArtistsRelease(r))
                .Where(r => filter.PrimaryTypes.Contains(r["release-group"]?["primary-type"]?.Value<string>() ?? ""))
                .Where(r => SecondaryTypesAllowed(r["release-group"], filter.SecondaryTypes))
                .Where(r => filter.Statuses.Contains(r["status"]?.Value<string>() ?? ""))
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            // Approved lexicographic ranking (PLAN §6.6): artist-credit match, then release
            // status, then primary type, then secondary type, then earliest release date.
            return candidates
                .OrderBy(r => recordingArtistId.IsNotNullOrWhiteSpace() && MatchesRecordingArtist(r, recordingArtistId) ? 0 : 1)
                .ThenBy(r => ReleaseStatusRank(r["status"]?.Value<string>()))
                .ThenBy(r => PrimaryTypeRank(r["release-group"]))
                .ThenBy(r => SecondaryTypeRank(r["release-group"]))
                .ThenBy(r => r["release-group"]?["first-release-date"]?.Value<string>() ?? "9999")
                .Select(r => r["release-group"])
                .FirstOrDefault();
        }

        // Mirrors Lidarr's SkyHookProxy.FilterAlbums rule: no secondary types means "Studio", so
        // it's allowed only when the profile permits Studio; a release-group with secondary types
        // is allowed when ANY of them is in the profile's allowed set.
        private static bool SecondaryTypesAllowed(JToken? releaseGroup, HashSet<string> allowedSecondaryTypes)
        {
            var secondaryTypes = releaseGroup?["secondary-types"] as JArray;
            if (secondaryTypes == null || secondaryTypes.Count == 0)
            {
                return allowedSecondaryTypes.Contains("Studio");
            }

            return secondaryTypes.Any(t => allowedSecondaryTypes.Contains(t.Value<string>() ?? ""));
        }

        // Approved ranking ladder (PLAN §6.6): lower = better.
        private static int ReleaseStatusRank(string? status)
        {
            return status switch
            {
                "Official" => 0,
                "Promotion" => 1,
                "Bootleg" => 2,
                "Pseudo-Release" => 3,
                _ => 4
            };
        }

        // For a played track, the release that most directly corresponds to it is the single, then
        // the EP, then the studio album - so rank primary types in that order.
        private static int PrimaryTypeRank(JToken? releaseGroup)
        {
            return (releaseGroup?["primary-type"]?.Value<string>()) switch
            {
                "Single" => 0,
                "EP" => 1,
                "Album" => 2,
                "Broadcast" => 3,
                "Other" => 4,
                _ => 5
            };
        }

        // Secondary-type ladder (PLAN §6.6): Studio first, Compilation strictly last.
        private static int SecondaryTypeRank(JToken? releaseGroup)
        {
            var secondaryTypes = releaseGroup?["secondary-types"] as JArray;
            if (secondaryTypes == null || secondaryTypes.Count == 0)
            {
                return 0;
            }

            return secondaryTypes
                .Select(t => t.Value<string>())
                .Where(t => t.IsNotNullOrWhiteSpace())
                .Select(t => SecondaryTypeRank(t!))
                .DefaultIfEmpty(0)
                .Max();
        }

        private static int SecondaryTypeRank(string type)
        {
            return type switch
            {
                "Studio" => 0,
                "Soundtrack" => 1,
                "Remix" => 2,
                "DJ-mix" => 3,
                "Compilation" => 4,
                _ => 5
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

        private static bool MatchesRecordingArtist(JToken release, string? recordingArtistId)
        {
            if (recordingArtistId.IsNullOrWhiteSpace())
            {
                return false;
            }

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

        private AlbumResolution? ResolveViaAppleMusic(string artist, IReadOnlyDictionary<string, string> links, AlbumTypeFilter filter)
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

            // Try to upgrade the Apple title to a real MBID before falling back to the raw title.
            var viaTitleSearch = ResolveViaMusicBrainzTitleSearch(artist, albumTitle, filter);
            if (viaTitleSearch != null)
            {
                return viaTitleSearch;
            }

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

    /// <summary>
    /// The set of release-group attributes the importing list's metadata profile allows. Candidates
    /// outside these sets are filtered out before ranking (mirroring Lidarr's own
    /// <c>SkyHookProxy.FilterAlbums</c>), so a profile that only allows Official/Studio/Single/EP/Album
    /// never returns a Bootleg or Live release just because it ranks last.
    /// </summary>
    public class AlbumTypeFilter
    {
        public static readonly AlbumTypeFilter Unrestricted = new(
            new HashSet<string> { "Single", "EP", "Album", "Broadcast", "Other" },
            new HashSet<string> { "Studio", "Compilation", "Soundtrack", "Spokenword", "Interview", "Audiobook", "Live", "Remix", "DJ-mix", "Mixtape/Street", "Demo", "Audio drama" },
            new HashSet<string> { "Official", "Promotion", "Bootleg", "Pseudo-Release" });

        public AlbumTypeFilter(HashSet<string> primaryTypes, HashSet<string> secondaryTypes, HashSet<string> statuses)
        {
            PrimaryTypes = primaryTypes;
            SecondaryTypes = secondaryTypes;
            Statuses = statuses;
        }

        public HashSet<string> PrimaryTypes { get; }
        public HashSet<string> SecondaryTypes { get; }
        public HashSet<string> Statuses { get; }
    }
}
