using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
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
    /// 1. Deezer link -&gt; Deezer's public API, which returns both an ISRC and Deezer's own album
    ///    title in the same call:
    ///    1a. ISRC -&gt; MusicBrainz's exact ISRC lookup -&gt; a real MusicBrainz release-group. This is
    ///        the precise path: an ISRC identifies one specific recording, not a fuzzy text match,
    ///        so we can hand Lidarr real MusicBrainz IDs directly.
    ///    1b. If that MusicBrainz path doesn't pan out, fall back to a MusicBrainz recording search
    ///        (artist candidates + song title), then a release-group title search (artist + Deezer's
    ///        album title) before settling for Deezer's own album title - we already paid for that
    ///        API call, no reason to throw its answer away and spend a second call on Apple before
    ///        trying it.
    /// 2. Apple Music link -&gt; iTunes Lookup API -&gt; a real album title (no MusicBrainz ID), then the
    ///    same title-search fallback. Used only when there's no Deezer link at all, or Deezer's
    ///    track has no album title either.
    /// 3. Title floor -&gt; Deezer's or Apple's album title with no MusicBrainz ID; Lidarr resolves
    ///    it via its own fuzzy search.
    ///
    /// Resolution is per release priority (Single vs Album), so a track can resolve to different
    /// releases for different lists. Results are cached by the caller (SXMPlaylistHistoryStore,
    /// keyed by track id + priority) since the same song replays constantly on a rotation-heavy
    /// station and its album never changes between plays.
    /// </summary>
    public class SXMPlaylistAlbumResolver
    {
        private static readonly Regex DeezerTrackId = new(@"deezer\.com/track/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AppleAlbumId = new(@"/album/[^/]+/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // MusicBrainz's entity for the "Various Artists" pseudo-artist that every compilation is
        // credited to. Compilations are useless for our purpose - a played track should resolve to
        // the artist's own release, not whichever "Summer Hits" compilation it also appears on.
        private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

        // 1.1s pacing keeps the production worker inside MusicBrainz's 1 req/s fair-use limit.
        // Mutable so the test harness can zero it (the fake HTTP client doesn't touch MusicBrainz,
        // and 300+ throttled calls would otherwise make the suite take minutes).
        internal static TimeSpan MusicBrainzMinInterval = TimeSpan.FromSeconds(1.1);
        private static readonly int MusicBrainzMaxRetries = 2;
        private const int MusicBrainzTitleSearchLimit = 25;
        private const int MusicBrainzRecordingSearchLimit = 10;
        private const int MusicBrainzRecordingDetailLimit = 3;
        private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
        private static DateTime _lastMusicBrainzCallUtc = DateTime.MinValue;

        // Only touched when a retry fires; small enough to keep tests fast.
        internal static TimeSpan MusicBrainzRetryBackoff = TimeSpan.FromSeconds(2);

        private const string UserAgent = "SXMPlaylist-Lidarr-Plugin/1.0 (https://github.com/ksamples14/lidarr.plugin.sxmplaylist)";
        internal static readonly ReleasePriorityMode[] ReleasePriorities = { ReleasePriorityMode.Singles, ReleasePriorityMode.Albums };

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;
        private bool _hadTransientMbFailure;

        public SXMPlaylistAlbumResolver(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        // Returns true when the most recent ResolveAllPriorities call failed transiently (MB
        // 503/429 that survived in-resolver sub-retries), false for permanent failures (404, no
        // data, connection errors from non-MB providers, etc.). Only meaningful when
        // ResolveAllPriorities returned no valid resolutions.
        public bool GetLastFailureTransient()
        {
            return _hadTransientMbFailure;
        }

        public AlbumResolution Resolve(
            string artist,
            string song,
            IReadOnlyDictionary<string, string> links,
            AlbumTypeFilter? filter = null,
            IReadOnlyList<string>? artistFragments = null,
            CancellationToken token = default)
        {
            var effectiveFilter = filter ?? AlbumTypeFilter.Unrestricted;
            var results = ResolveAllPriorities(artist, song, links, effectiveFilter, artistFragments, token);
            return results.TryGetValue(effectiveFilter.ReleasePriority, out var resolution) ? resolution : AlbumResolution.NotFound;
        }

        public IReadOnlyDictionary<ReleasePriorityMode, AlbumResolution> ResolveAllPriorities(
            string artist,
            string song,
            IReadOnlyDictionary<string, string> links,
            AlbumTypeFilter? filter = null,
            IReadOnlyList<string>? artistFragments = null,
            CancellationToken token = default)
        {
            var effectiveFilter = filter ?? AlbumTypeFilter.Unrestricted;
            var results = new Dictionary<ReleasePriorityMode, AlbumResolution>();
            string? deezerAlbumTitle = null;
            string? appleAlbumTitle = null;
            string? deezerArtist = null;
            var appleLookupAttempted = false;
            _hadTransientMbFailure = false;

            try
            {
                token.ThrowIfCancellationRequested();
                var deezerTrack = GetDeezerTrack(artist, links, token);
                deezerAlbumTitle = deezerTrack?["album"]?["title"]?.Value<string>();
                deezerArtist = deezerTrack?["artist"]?["name"]?.Value<string>();

                foreach (var result in ResolveViaMusicBrainzAll(deezerTrack, deezerArtist, effectiveFilter, token))
                {
                    _logger.Debug("Resolved {0} via Deezer ISRC to {1} MusicBrainz album '{2}' ({3})", artist, result.Key, result.Value.Album, result.Value.AlbumMusicBrainzId);
                    results[result.Key] = result.Value;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpException ex)
            {
                _logger.Debug(ex, "Deezer/MusicBrainz album lookup failed for {0} - {1}", artist, song);
            }
            catch (JsonReaderException ex)
            {
                _logger.Debug(ex, "Deezer/MusicBrainz album lookup failed for {0} - {1}", artist, song);
            }

            var artistCandidates = BuildArtistCandidates(artist, artistFragments, deezerArtist);

            if (!HasAllPriorities(results))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var result in ResolveViaMusicBrainzRecordingSearchAll(artistCandidates, song, effectiveFilter, token))
                    {
                        if (!results.ContainsKey(result.Key))
                        {
                            _logger.Debug("Resolved {0} via MusicBrainz recording search to {1} MusicBrainz album '{2}' ({3})", artist, result.Key, result.Value.Album, result.Value.AlbumMusicBrainzId);
                            results[result.Key] = result.Value;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpException ex)
                {
                    _hadTransientMbFailure = true;
                    _logger.Debug(ex, "MusicBrainz recording search fallback failed for {0}; trying title search", artist);
                }
                catch (JsonReaderException ex)
                {
                    _hadTransientMbFailure = true;
                    _logger.Debug(ex, "MusicBrainz recording search fallback failed for {0}; trying title search", artist);
                }
            }

            AddMissingTitleSearchResults(results, artistCandidates, deezerAlbumTitle, effectiveFilter, "Deezer", token);

            if (!HasAllPriorities(results) && links.ContainsKey("appleMusic"))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    appleLookupAttempted = true;
                    appleAlbumTitle = GetAppleAlbumTitle(artist, links, token);
                    AddMissingTitleSearchResults(results, artistCandidates, appleAlbumTitle, effectiveFilter, "Apple", token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpException ex)
                {
                    _logger.Debug(ex, "Apple Music album lookup failed for {0} - {1}", artist, song);
                }
                catch (JsonReaderException ex)
                {
                    _logger.Debug(ex, "Apple Music album lookup failed for {0} - {1}", artist, song);
                }
            }

            AddTitleOnlyFallback(results, deezerAlbumTitle);

            if (!HasAllPriorities(results) && links.ContainsKey("appleMusic"))
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!appleLookupAttempted)
                    {
                        appleLookupAttempted = true;
                        appleAlbumTitle = GetAppleAlbumTitle(artist, links);
                    }

                    AddTitleOnlyFallback(results, appleAlbumTitle);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpException ex)
                {
                    _logger.Debug(ex, "Apple Music title-only fallback failed for {0} - {1}", artist, song);
                }
                catch (JsonReaderException ex)
                {
                    _logger.Debug(ex, "Apple Music title-only fallback failed for {0} - {1}", artist, song);
                }
            }

            return results;
        }

        private static bool HasAllPriorities(IReadOnlyDictionary<ReleasePriorityMode, AlbumResolution> results)
        {
            return ReleasePriorities.All(results.ContainsKey);
        }

        // The feed can fragment a single artist name across separators ("Kool & The Gang" becomes
        // ['Kool', 'The Gang'], "M.A.R.R.S" becomes ['M','A','R','R','S']). Reconstruct candidate
        // artist strings - the Deezer artist name is the most reliable, then the space-joined
        // fragments, then the first fragment - so MusicBrainz searches are tried against every
        // plausible name until one produces matches. Individual trailing fragments are dropped:
        // a lone "Kool" or "The Gang" almost never passes ArtistCreditMatches against MB's
        // canonical "Kool & The Gang", so they'd only waste a rate-limited search.
        private static IReadOnlyList<string> BuildArtistCandidates(string artist, IReadOnlyList<string>? fragments, string? deezerArtist)
        {
            var candidates = new List<string>();
            void Add(string? name)
            {
                if (name.IsNullOrWhiteSpace())
                {
                    return;
                }

                var collapsed = string.Join(' ', name!.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (!candidates.Any(c => string.Equals(c, collapsed, StringComparison.OrdinalIgnoreCase)))
                {
                    candidates.Add(collapsed);
                }
            }

            Add(deezerArtist);
            if (fragments != null)
            {
                var joined = string.Join(" ", fragments.Where(f => f.IsNotNullOrWhiteSpace())).Trim();
                Add(joined);
            }

            Add(artist);

            if (candidates.Count == 0)
            {
                candidates.Add(artist);
            }

            return candidates;
        }

        private JToken? GetDeezerTrack(string artist, IReadOnlyDictionary<string, string> links, CancellationToken token = default)
        {
            if (!links.TryGetValue("deezer", out var deezerUrl))
            {
                _logger.Debug("No Deezer link for {0}", artist);
                return null;
            }

            var match = DeezerTrackId.Match(deezerUrl);
            if (!match.Success)
            {
                return null;
            }

            var track = GetJson($"https://api.deezer.com/track/{match.Groups[1].Value}", token: token);
            var deezerAlbumTitle = track?["album"]?["title"]?.Value<string>();
            _logger.Debug("Deezer lookup for {0} returned album title '{1}'", artist, deezerAlbumTitle ?? "<none>");
            return track;
        }

        private string? GetAppleAlbumTitle(string artist, IReadOnlyDictionary<string, string> links, CancellationToken token = default)
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

            var lookup = GetJson($"https://itunes.apple.com/lookup?id={match.Groups[1].Value}", token: token);
            var albumTitle = lookup?["results"]?.FirstOrDefault()?["collectionName"]?.Value<string>();
            _logger.Debug("Apple Music lookup for {0} returned album title '{1}'", artist, albumTitle ?? "<none>");
            return albumTitle;
        }

        private void AddMissingTitleSearchResults(
            Dictionary<ReleasePriorityMode, AlbumResolution> results,
            IReadOnlyList<string> artistCandidates,
            string? albumTitle,
            AlbumTypeFilter filter,
            string source,
            CancellationToken token = default)
        {
            if (HasAllPriorities(results) || albumTitle.IsNullOrWhiteSpace())
            {
                return;
            }

            try
            {
                foreach (var result in ResolveViaMusicBrainzTitleSearchAll(artistCandidates, albumTitle, filter))
                {
                    if (!results.ContainsKey(result.Key))
                    {
                        _logger.Debug("Resolved {0} via {1} title search to {2} MusicBrainz album '{3}' ({4})", string.Join(" / ", artistCandidates), source, result.Key, result.Value.Album, result.Value.AlbumMusicBrainzId);
                        results[result.Key] = result.Value;
                    }
                }
            }
            catch (HttpException ex)
            {
                _logger.Debug(ex, "MusicBrainz {0} title search fallback failed for {1}", source, string.Join(" / ", artistCandidates));
            }
            catch (JsonReaderException ex)
            {
                _logger.Debug(ex, "MusicBrainz {0} title search fallback failed for {1}", source, string.Join(" / ", artistCandidates));
            }
        }

        private static void AddTitleOnlyFallback(Dictionary<ReleasePriorityMode, AlbumResolution> results, string? albumTitle)
        {
            if (albumTitle.IsNullOrWhiteSpace())
            {
                return;
            }

            foreach (var priority in ReleasePriorities.Where(p => !results.ContainsKey(p)))
            {
                results[priority] = new AlbumResolution(true, albumTitle, null, null, resolutionMethod: "title-only");
            }
        }

        private Dictionary<ReleasePriorityMode, AlbumResolution> ResolveViaMusicBrainzRecordingSearchAll(
            IReadOnlyList<string> artistCandidates,
            string song,
            AlbumTypeFilter filter,
            CancellationToken token = default)
        {
            var results = new Dictionary<ReleasePriorityMode, AlbumResolution>();
            if (artistCandidates == null || artistCandidates.Count == 0 || song.IsNullOrWhiteSpace())
            {
                return results;
            }

            var releaseCandidates = new JArray();
            string? recordingArtistMbid = null;
            string? selectedRecordingMbid = null;
            var artistRejected = 0;
            var titleRejected = 0;
            var noRecordings = 0;

            foreach (var artist in artistCandidates)
            {
                if (artist.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // Per-candidate budget: a wrong-artist candidate that passes the title gate and
                // consumes all detail slots must not starve a later, correct candidate. A shared
                // global cap would let "Kool" burn the budget before "Kool & The Gang" is tried.
                var detailLookups = 0;

                var query = BuildRecordingSearchQuery(artist, song);
                ThrottleMusicBrainz(token);
                var search = GetJson($"https://musicbrainz.org/ws/2/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={MusicBrainzRecordingSearchLimit}", musicBrainz: true, token);
                var recordings = search?["recordings"] as JArray;
                if (recordings == null || recordings.Count == 0)
                {
                    noRecordings++;
                    continue;
                }

                // MusicBrainz relevance orders compilations above the artist's own release, so the
                // first recordings often only carry compilations and consume the detail budget below.
                // The search response already embeds each recording's releases with their
                // release-groups, so reorder before the detail cap: recordings that inline a
                // profile-eligible, non-compilation release-group are examined first, recordings
                // that only inline compilations/VA sink to the end, and unknown ones keep MB order.
                recordings = new JArray(
                    recordings
                        .Cast<JToken>()
                        .OrderBy(r => InlineReleasePriority(r, filter))
                        .ToList());

                foreach (var recording in recordings)
                {
                    var title = recording["title"]?.Value<string>();
                    if (title.IsNullOrWhiteSpace() || TitleSimilarity(title!, song) < TitleMatchThreshold)
                    {
                        titleRejected++;
                        continue;
                    }

                    if (!ArtistCreditMatches(recording, artist))
                    {
                        artistRejected++;
                        continue;
                    }

                    var recordingId = recording["id"]?.Value<string>();
                    if (recordingId.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    if (detailLookups >= MusicBrainzRecordingDetailLimit)
                    {
                        break;
                    }

                    detailLookups++;

                    ThrottleMusicBrainz(token);
                    var fullRecording = GetJson(
                        $"https://musicbrainz.org/ws/2/recording/{recordingId}?inc=releases+release-groups+artist-credits&fmt=json",
                        musicBrainz: true,
                        token);
                    var releases = fullRecording?["releases"] as JArray;
                    if (releases == null || releases.Count == 0)
                    {
                        continue;
                    }

                    selectedRecordingMbid ??= recordingId;

                    var artistCredits = fullRecording?["artist-credit"] as JArray;
                    if (recordingArtistMbid == null && artistCredits is { Count: 1 })
                    {
                        recordingArtistMbid = artistCredits[0]["artist"]?["id"]?.Value<string>();
                    }

                    foreach (var release in releases)
                    {
                        releaseCandidates.Add(release);
                    }
                }

                // Only stop trying artist candidates when we actually accumulated a release that
                // can survive release-group selection. releaseCandidates holds raw releases; the
                // VA/compilation/profile gates run later inside SelectBestReleaseGroup, so a
                // candidate whose only releases are VA compilations must not consume the budget
                // and starve a later, correct artist candidate ("Kool" vs "Kool & The Gang").
                if (releaseCandidates.Count > 0 && HasEligibleReleaseGroup(releaseCandidates, filter))
                {
                    break;
                }
            }

            if (releaseCandidates.Count == 0)
            {
                _logger.Debug(
                    "MusicBrainz recording search rejected all recordings for '{0}' ({1} artist-credit, {2} title, {3} empty searches)",
                    song,
                    artistRejected,
                    titleRejected,
                    noRecordings);
                return results;
            }

            var syntheticRecording = new JObject { ["releases"] = releaseCandidates };
            foreach (var priority in ReleasePriorities)
            {
                var releaseGroup = SelectBestReleaseGroup(syntheticRecording, recordingArtistMbid, filter.WithReleasePriority(priority), _logger);
                var resolution = BuildResolution(releaseGroup, recordingArtistMbid, selectedRecordingMbid, null, "recording-search");
                if (resolution != null)
                {
                    results[priority] = resolution;
                }
            }

            return results;
        }

        private Dictionary<ReleasePriorityMode, AlbumResolution> ResolveViaMusicBrainzAll(JToken? track, string? deezerArtist, AlbumTypeFilter filter, CancellationToken token = default)
        {
            var results = new Dictionary<ReleasePriorityMode, AlbumResolution>();
            var isrc = track?["isrc"]?.Value<string>();
            if (isrc.IsNullOrWhiteSpace())
            {
                _logger.Debug("Deezer track has no ISRC; cannot use exact MusicBrainz lookup");
                return results;
            }

            ThrottleMusicBrainz(token);
            var isrcResult = GetJson($"https://musicbrainz.org/ws/2/isrc/{isrc}?fmt=json", musicBrainz: true, token);

            // MusicBrainz ISRC lookups can return multiple recordings (mis-credited duplicates,
            // re-issues). Prefer the one whose artist-credit matches the played/Deezer artist so a
            // wrong-artist recording can't be attached with real MBIDs; fall back to the first.
            var recordings = isrcResult?["recordings"] as JArray;
            var recordingId = recordings?
                .FirstOrDefault(r => ArtistCreditMatches(r, deezerArtist))?["id"]?.Value<string>()
                ?? recordings?.FirstOrDefault()?["id"]?.Value<string>();

            if (recordingId.IsNullOrWhiteSpace())
            {
                _logger.Debug("MusicBrainz ISRC lookup found no recording for ISRC {0}", isrc);
                return results;
            }

            ThrottleMusicBrainz(token);
            var recording = GetJson(
                $"https://musicbrainz.org/ws/2/recording/{recordingId}?inc=releases+release-groups+artist-credits&fmt=json",
                musicBrainz: true,
                token);

            var artistCredits = recording?["artist-credit"] as JArray;
            string? artistMbid = artistCredits is { Count: 1 } ? artistCredits[0]["artist"]?["id"]?.Value<string>() : null;

            foreach (var priority in ReleasePriorities)
            {
                var releaseGroup = SelectBestReleaseGroup(recording, artistMbid, filter.WithReleasePriority(priority), _logger);
                var resolution = BuildResolution(releaseGroup, artistMbid, recordingId, isrc, "isrc");
                if (resolution != null)
                {
                    results[priority] = resolution;
                }
            }

            if (results.Count == 0)
            {
                _logger.Debug("MusicBrainz recording {0} had no acceptable release-group for recording artist {1}", recordingId, artistMbid ?? "<none>");
            }

            return results;
        }

        private static AlbumResolution? BuildResolution(
            JToken? releaseGroup,
            string? artistMbid,
            string? recordingMbid = null,
            string? isrc = null,
            string? resolutionMethod = null)
        {
            var albumTitle = releaseGroup?["title"]?.Value<string>();
            var albumMbid = releaseGroup?["id"]?.Value<string>();

            return albumTitle.IsNullOrWhiteSpace() || albumMbid.IsNullOrWhiteSpace()
                ? null
                : new AlbumResolution(true, albumTitle, artistMbid, albumMbid, recordingMbid, isrc, resolutionMethod);
        }

        // Fallback after the exact ISRC path misses: search MusicBrainz release-groups by the
        // artist + the album title we already got (from Deezer or Apple), gated by a fuzzy
        // artist-credit match and a title-similarity threshold so a same-titled album by a
        // different artist can't be attached. Returns real MBIDs when matches are confident.
        private Dictionary<ReleasePriorityMode, AlbumResolution> ResolveViaMusicBrainzTitleSearchAll(
            IReadOnlyList<string> artistCandidates,
            string? albumTitle,
            AlbumTypeFilter filter,
            CancellationToken token = default)
        {
            var results = new Dictionary<ReleasePriorityMode, AlbumResolution>();
            if (artistCandidates == null || artistCandidates.Count == 0 || albumTitle.IsNullOrWhiteSpace())
            {
                return results;
            }

            var candidates = new JArray();
            var artistRejected = 0;
            var titleRejected = 0;
            var noReleases = 0;

            foreach (var artist in artistCandidates)
            {
                if (artist.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var query = BuildTitleSearchQuery(artist, albumTitle!);
                ThrottleMusicBrainz();
                var result = GetJson($"https://musicbrainz.org/ws/2/release?query={Uri.EscapeDataString(query)}&fmt=json&limit={MusicBrainzTitleSearchLimit}", musicBrainz: true, token);

                var releases = result?["releases"] as JArray;
                if (releases == null || releases.Count == 0)
                {
                    noReleases++;
                    continue;
                }

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
                        artistRejected++;
                        continue;
                    }

                    // Title gate: release-group title must be similar enough to the Deezer/Apple title.
                    var rgTitle = rg["title"]?.Value<string>();
                    if (rgTitle.IsNullOrWhiteSpace() || TitleSimilarity(rgTitle!, albumTitle!) < TitleMatchThreshold)
                    {
                        titleRejected++;
                        continue;
                    }

                    candidates.Add(release);
                }

                if (candidates.Count > 0)
                {
                    break;
                }
            }

            if (candidates.Count == 0)
            {
                _logger.Debug(
                    "MusicBrainz title search rejected all releases for '{0}' ({1} artist-credit, {2} title, {3} empty searches)",
                    albumTitle,
                    artistRejected,
                    titleRejected,
                    noReleases);
                return results;
            }

            _logger.Debug(
                "MusicBrainz title search for '{0}' kept {1} releases ({2} artist-credit rejects, {3} title rejects)",
                albumTitle,
                candidates.Count,
                artistRejected,
                titleRejected);

            // Reuse the approved ranking (status > primary > secondary > date, VA excluded).
            var syntheticRecording = new JObject { ["releases"] = candidates };
            foreach (var priority in ReleasePriorities)
            {
                var selectedReleaseGroup = SelectBestReleaseGroup(syntheticRecording, null, filter.WithReleasePriority(priority), _logger);
                if (selectedReleaseGroup == null)
                {
                    continue;
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
                    continue;
                }

                _logger.Debug("MusicBrainz title search selected '{0}' ({1}) for {2} / '{3}' using {4} priority", finalTitle, finalAlbumMbid, string.Join(" / ", artistCandidates), albumTitle, priority);
                results[priority] = new AlbumResolution(true, finalTitle, artistMbid, finalAlbumMbid, resolutionMethod: "title-search");
            }

            if (results.Count == 0)
            {
                _logger.Debug("MusicBrainz title search candidates for {0} / '{1}' were filtered out by release type/status", string.Join(" / ", artistCandidates), albumTitle);
            }

            return results;
        }

        // Boosted-OR form (DroppedNeedle pattern): phrase boost the title, fall back to unquoted
        // tokens, all AND'd against the artist so recall stays high while precision lives in code.
        // The edition suffix and bare year are stripped from the query too - a "Emergency (85)" or
        // "Three Cheers (Deluxe Edition)" phrase won't match MusicBrainz's clean "Emergency" /
        // "Three Cheers for Sweet Revenge" otherwise.
        private static string BuildTitleSearchQuery(string artist, string albumTitle)
        {
            var stripped = SXMPlaylistTitleNormalizer.StripEditionAndYearSuffixes(albumTitle);
            if (stripped.IsNullOrWhiteSpace())
            {
                stripped = albumTitle;
            }

            var title = EscapeLucene(stripped);
            var artistQuery = EscapeLucene(artist);
            return $"(releasegroup:\"{title}\"^3 OR release:\"{title}\"^2 OR {title}) AND artist:\"{artistQuery}\"";
        }

        private static string BuildRecordingSearchQuery(string artist, string song)
        {
            var stripped = SXMPlaylistTitleNormalizer.StripEditionAndYearSuffixes(song);
            if (stripped.IsNullOrWhiteSpace())
            {
                stripped = song;
            }

            var title = EscapeLucene(stripped);
            var artistQuery = EscapeLucene(artist);
            return $"(recording:\"{title}\"^3 OR {title}) AND artist:\"{artistQuery}\"";
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
        private static bool ArtistCreditMatches(JToken release, string? playedArtist)
        {
            var credit = release["artist-credit"] as JArray;
            if (credit == null || credit.Count == 0 || playedArtist.IsNullOrWhiteSpace())
            {
                // No artist data to compare against — can't reject on this basis.
                return true;
            }

            foreach (var entry in credit)
            {
                var name = entry["artist"]?["name"]?.Value<string>();
                if (name.IsNotNullOrWhiteSpace() && ArtistSimilarity(name!, playedArtist!) >= ArtistMatchFloor)
                {
                    return true;
                }
            }

            return false;
        }

        private const double TitleMatchThreshold = 0.85;
        private const double ArtistMatchFloor = 0.6;

        private static string NormalizeForMatch(string value)
        {
            value = SXMPlaylistTitleNormalizer.StripEditionAndYearSuffixes(value);

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
            // "best of" ⊂ "the best of" scores highly like token_set_ratio. Do not let a
            // one-token candidate title match only because it appears inside a longer provider
            // title: that accepts false self-titled hits like Cake/moZart.
            var smaller = Math.Min(tokensA.Count, tokensB.Count);
            var containment = smaller < 2 ? 0.0 : (double)intersection / smaller;

            var editScore = 1.0 - (double)LevenshteinDistance(na, nb) / Math.Max(na.Length, nb.Length);

            return Math.Max(Math.Max(tokenScore, containment), editScore);
        }

        // Similarity of two artist names against a looser floor (display names vary more than
        // album titles). Uses containment of the shorter name in the longer as a token-set proxy.
        private static double ArtistSimilarity(string a, string b)
        {
            var na = StripLeadingArticle(NormalizeForMatch(a));
            var nb = StripLeadingArticle(NormalizeForMatch(b));
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

            // Containment of the shorter name in the longer: "police" ⊂ "the police" (after the
            // article strip) scores 1.0 even though the plain Jaccard would be 0.5 — leading
            // articles are extremely common in artist names and must not fail the artist gate.
            var tokenScore = union == 0 ? 0.0 : (double)intersection / union;
            var smaller = Math.Min(tokensA.Count, tokensB.Count);
            var containment = smaller == 0 ? 0.0 : (double)intersection / smaller;
            return Math.Max(tokenScore, containment);
        }

        // Strips a leading "the/a/an" so "The Police" compares equal to "Police". Applied only to
        // artist matching — album titles keep their articles.
        private static string StripLeadingArticle(string normalized)
        {
            if (normalized.StartsWith("the ", StringComparison.Ordinal))
            {
                return normalized[4..];
            }

            if (normalized.StartsWith("a ", StringComparison.Ordinal))
            {
                return normalized[2..];
            }

            if (normalized.StartsWith("an ", StringComparison.Ordinal))
            {
                return normalized[3..];
            }

            return normalized;
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

        private static JToken? SelectBestReleaseGroup(
            JToken? recording,
            string? recordingArtistId,
            AlbumTypeFilter filter,
            Logger? logger = null)
        {
            var releases = recording?["releases"] as JArray;
            if (releases == null || releases.Count == 0)
            {
                return null;
            }

            // Various Artists releases are hard-excluded - a VA compilation's release-group belongs
            // to the pseudo-artist, and handing Lidarr that album attaches the play to the wrong
            // artist. Same-artist compilations remain eligible only as a fallback below.
            var candidates = releases
                .Where(r => r["release-group"] != null)
                .Where(r => !IsVariousArtistsRelease(r))
                .Where(r => filter.PrimaryTypes.Contains(r["release-group"]?["primary-type"]?.Value<string>() ?? ""))
                .Where(r => SecondaryTypesAllowed(r["release-group"], filter.SecondaryTypes))
                .Where(r => filter.Statuses.Contains(r["status"]?.Value<string>() ?? ""))
                .ToList();

            candidates = FilterByRecordingArtist(candidates, recordingArtistId, logger);

            if (candidates.Count > 0)
            {
                return SelectRankedReleaseGroup(candidates, recordingArtistId, filter);
            }

            // Metadata profiles often exclude Compilation, but artist-owned compilations can still
            // be the only correct home for radio-played non-album/B-side tracks. Keep them behind
            // every profile-allowed candidate, and never relax primary type, status, or VA gates.
            // The !SecondaryTypesAllowed check keeps this fallback scoped to release-groups whose
            // secondary type was the reason they were excluded.
            candidates = releases
                .Where(r => r["release-group"] != null)
                .Where(r => !IsVariousArtistsRelease(r))
                .Where(r => filter.PrimaryTypes.Contains(r["release-group"]?["primary-type"]?.Value<string>() ?? ""))
                .Where(r => !SecondaryTypesAllowed(r["release-group"], filter.SecondaryTypes))
                .Where(r => IsCompilationReleaseGroup(r["release-group"]))
                .Where(r => filter.Statuses.Contains(r["status"]?.Value<string>() ?? ""))
                .ToList();

            candidates = FilterByRecordingArtist(candidates, recordingArtistId, logger);

            if (candidates.Count == 0)
            {
                return null;
            }

            return SelectRankedReleaseGroup(candidates, recordingArtistId, filter);
        }

        private static List<JToken> FilterByRecordingArtist(List<JToken> candidates, string? recordingArtistId, Logger? logger)
        {
            if (recordingArtistId.IsNotNullOrWhiteSpace())
            {
                var creditedCandidates = candidates.Where(r => FirstCreditedArtist(r) != null).ToList();
                var sameArtistCandidates = creditedCandidates.Where(r => MatchesRecordingArtist(r, recordingArtistId)).ToList();

                if (sameArtistCandidates.Count > 0)
                {
                    candidates = sameArtistCandidates;
                }
                else if (creditedCandidates.Count > 0)
                {
                    logger?.Debug(
                        "Rejected {0} MusicBrainz release candidates because none matched recording artist {1}",
                        creditedCandidates.Count,
                        recordingArtistId);
                    return new List<JToken>();
                }
            }

            return candidates;
        }

        private static JToken? SelectRankedReleaseGroup(List<JToken> candidates, string? recordingArtistId, AlbumTypeFilter filter)
        {
            // Approved lexicographic ranking (PLAN §6.6): artist-credit match, then release
            // status, then primary type, then secondary type, then earliest release date.
            return candidates
                .OrderBy(r => recordingArtistId.IsNotNullOrWhiteSpace() && MatchesRecordingArtist(r, recordingArtistId) ? 0 : 1)
                .ThenBy(r => ReleaseStatusRank(r["status"]?.Value<string>()))
                .ThenBy(r => PrimaryTypeRank(r["release-group"], filter.ReleasePriority))
                .ThenBy(r => SecondaryTypeRank(r["release-group"]))
                .ThenBy(r => r["release-group"]?["first-release-date"]?.Value<string>() ?? "9999")
                .Select(r => r["release-group"])
                .FirstOrDefault();
        }

        private static bool IsCompilationReleaseGroup(JToken? releaseGroup)
        {
            var secondaryTypes = releaseGroup?["secondary-types"] as JArray;
            return secondaryTypes != null && secondaryTypes.Any(t => string.Equals(t.Value<string>(), "Compilation", StringComparison.OrdinalIgnoreCase));
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

        // Sort key used to reorder recording-search results before the detail-lookup cap. MusicBrainz
        // relevance puts compilations first, so we examine recordings whose inline releases include a
        // profile-eligible, non-compilation release-group ahead of ones that only carry compilations
        // or Various Artists releases. Lower = examined first. This uses only the releases already
        // embedded in the search response, so it costs no extra MusicBrainz calls and works per
        // artist candidate (the detail budget below is per candidate).
        private static int InlineReleasePriority(JToken recording, AlbumTypeFilter filter)
        {
            var inlineReleases = recording["releases"] as JArray;
            if (inlineReleases == null || inlineReleases.Count == 0)
            {
                return 1;
            }

            var hasEligible = false;
            foreach (var release in inlineReleases)
            {
                var releaseGroup = release["release-group"];
                if (releaseGroup == null)
                {
                    continue;
                }

                var primaryType = releaseGroup["primary-type"]?.Value<string>() ?? "";
                if (!filter.PrimaryTypes.Contains(primaryType))
                {
                    continue;
                }

                if (!filter.Statuses.Contains(release["status"]?.Value<string>() ?? ""))
                {
                    continue;
                }

                if (IsVariousArtistsRelease(release))
                {
                    continue;
                }

                var secondaryTypes = releaseGroup["secondary-types"] as JArray;
                var isCompilation = secondaryTypes != null
                                    && secondaryTypes.Any(t => string.Equals(t.Value<string>(), "Compilation", StringComparison.OrdinalIgnoreCase));

                // A release-group with no secondary types counts as Studio; allow the profile's
                // non-compilation secondary types (Studio, Live, ...) to count as eligible too.
                var isEligibleSecondary = secondaryTypes == null || secondaryTypes.Count == 0
                    ? filter.SecondaryTypes.Contains("Studio")
                    : !isCompilation && secondaryTypes.Any(t => filter.SecondaryTypes.Contains(t.Value<string>() ?? ""));

                if (isEligibleSecondary)
                {
                    hasEligible = true;
                }
            }

            return hasEligible ? 0 : 2;
        }

        // True when at least one accumulated release survives the gates SelectBestReleaseGroup will
        // apply later (profile primary/status types, VA exclusion, and either an allowed secondary
        // type or the same-artist compilation fallback). Used to decide whether a recording-search
        // candidate is worth stopping on — see the early-break in
        // ResolveViaMusicBrainzRecordingSearchAll.
        private static bool HasEligibleReleaseGroup(IEnumerable<JToken> releaseCandidates, AlbumTypeFilter filter)
        {
            foreach (var release in releaseCandidates)
            {
                var releaseGroup = release["release-group"];
                if (releaseGroup == null)
                {
                    continue;
                }

                var primaryType = releaseGroup["primary-type"]?.Value<string>() ?? "";
                if (!filter.PrimaryTypes.Contains(primaryType))
                {
                    continue;
                }

                if (!filter.Statuses.Contains(release["status"]?.Value<string>() ?? ""))
                {
                    continue;
                }

                if (IsVariousArtistsRelease(release))
                {
                    continue;
                }

                // Pass 1: a secondary type allowed by the profile (no secondary types counts as
                // Studio) — mirrors SelectBestReleaseGroup's first candidate pass.
                if (SecondaryTypesAllowed(releaseGroup, filter.SecondaryTypes))
                {
                    return true;
                }

                // Pass 2: same-artist compilations whose secondary type the profile excludes are
                // still eligible as a last resort (mirrors SelectBestReleaseGroup's fallback pass).
                // Skipping this here would falsely continue the artist loop for compilation-only
                // candidates and let the next candidate's budget starve the correct one.
                if (!SecondaryTypesAllowed(releaseGroup, filter.SecondaryTypes) && IsCompilationReleaseGroup(releaseGroup))
                {
                    return true;
                }
            }

            return false;
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

        // For a played track, singles-first keeps the most direct radio release by default. Older
        // channels can flip to albums-first when the canonical studio album is usually preferred.
        private static int PrimaryTypeRank(JToken? releaseGroup, ReleasePriorityMode releasePriority)
        {
            var primaryType = releaseGroup?["primary-type"]?.Value<string>();
            if (releasePriority == ReleasePriorityMode.Albums)
            {
                return primaryType switch
                {
                    "Album" => 0,
                    "EP" => 1,
                    "Single" => 2,
                    "Broadcast" => 3,
                    "Other" => 4,
                    _ => 5
                };
            }

            return primaryType switch
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

        private JToken? GetJson(string url, bool musicBrainz = false, CancellationToken token = default)
        {
            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add("User-Agent", UserAgent);

            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();

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
                    // If we exhausted MB retries on a 503/429, flag it as transient so the
                    // caller can schedule a later re-attempt instead of giving up permanently.
                    if (musicBrainz
                        && attempt >= MusicBrainzMaxRetries
                        && (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                            || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests))
                    {
                        _hadTransientMbFailure = true;
                    }
                    return null;
                }

                var backoff = MusicBrainzRetryBackoff * (attempt + 1);
                _logger.Debug("MusicBrainz returned {0}, retrying in {1}ms", response.StatusCode, backoff.TotalMilliseconds);
                if (token.WaitHandle.WaitOne(backoff))
                {
                    token.ThrowIfCancellationRequested();
                }
            }
        }

        private static void ThrottleMusicBrainz(CancellationToken token = default)
        {
            MusicBrainzGate.Wait(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var elapsed = DateTime.UtcNow - _lastMusicBrainzCallUtc;
                if (elapsed < MusicBrainzMinInterval)
                {
                    var wait = MusicBrainzMinInterval - elapsed;
                    if (token.WaitHandle.WaitOne(wait))
                    {
                        token.ThrowIfCancellationRequested();
                    }
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
            new HashSet<string> { "Official", "Promotion", "Bootleg", "Pseudo-Release" },
            ReleasePriorityMode.Singles);

        public AlbumTypeFilter(
            HashSet<string> primaryTypes,
            HashSet<string> secondaryTypes,
            HashSet<string> statuses,
            ReleasePriorityMode releasePriority = ReleasePriorityMode.Singles)
        {
            PrimaryTypes = primaryTypes;
            SecondaryTypes = secondaryTypes;
            Statuses = statuses;
            ReleasePriority = releasePriority;
        }

        public HashSet<string> PrimaryTypes { get; }
        public HashSet<string> SecondaryTypes { get; }
        public HashSet<string> Statuses { get; }
        public ReleasePriorityMode ReleasePriority { get; }

        public AlbumTypeFilter WithReleasePriority(ReleasePriorityMode releasePriority)
        {
            return new AlbumTypeFilter(PrimaryTypes, SecondaryTypes, Statuses, releasePriority);
        }
    }
}
