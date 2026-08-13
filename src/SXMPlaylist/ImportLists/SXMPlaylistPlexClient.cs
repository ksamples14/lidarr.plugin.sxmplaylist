using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Plex.Server;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// Builds a companion Plex audio playlist for an import list. Reuses the Plex Media Server
    /// connection the user configured in Settings > Connect (host/port/ssl/urlBase/authToken),
    /// so no Plex credentials are stored here.
    ///
    /// Design notes:
    /// - Best-effort and inert: no Plex connection, checkbox off, or auth failure -> nothing
    ///   happens, and the import flow is never failed.
    /// - Track -> Plex ratingKey is resolved by searching the Plex music library per unique
    ///   (artist, title) with two-tier matching: an exact tier (feat-credit-stripped only) first,
    ///   then a fuzzy tier that also tolerates version/remaster/live suffixes. Multi-artist plays
    ///   try each credited artist. Matched pairs are cached across syncs via PlexPlaylistState's
    ///   TrackCacheJson so a repeated track in the window is only searched once.
    /// - The playlist ratingKey is persisted in PlexPlaylistState (keyed by import list id) so we
    ///   only ever touch playlists we created; find-by-title is the first-run fallback.
    /// - Contents are replaced (clear + batched add) only when the target ratingKey sequence
    ///   differs from what Plex already holds, so an unchanged playlist is a no-op.
    /// </summary>
    public class SXMPlaylistPlexClient
    {
        private const int AddBatchSize = 100;
        private const int SearchAttempts = 3;
        private static readonly int[] RetryDelaysMs = { 0, 700, 1500 };

        private readonly IHttpClient _httpClient;
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        // Per-pass caches (reset per Sync call).
        private readonly Dictionary<string, string?> _trackRatingKeyCache = new(StringComparer.OrdinalIgnoreCase);
        private string? _cachedMachineId;
        private List<long>? _cachedMusicSectionIds;

        public SXMPlaylistPlexClient(IHttpClient httpClient, INotificationFactory notificationFactory, Logger logger)
        {
            _httpClient = httpClient;
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        // Seeded from the persisted per-list cache (see SXMPlaylistHistoryStore) so repeated syncs
        // reuse already-matched (artist, title) -> ratingKey pairs instead of re-searching Plex.
        public void SeedTrackCache(IReadOnlyDictionary<string, string> cache)
        {
            foreach (var pair in cache)
            {
                _trackRatingKeyCache[pair.Key] = pair.Value;
            }
        }

        // Returns the non-empty (artist||title) -> ratingKey pairs found this pass so the caller can
        // persist them.
        public IReadOnlyDictionary<string, string> ExportTrackCache()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _trackRatingKeyCache)
            {
                if (pair.Value.IsNotNullOrWhiteSpace())
                {
                    result[pair.Key] = pair.Value!;
                }
            }

            return result;
        }

        public PlexServerSettings? FindPlexSettings()
        {
            try
            {
                return _notificationFactory.GetAvailableProviders()
                    .Select(p => p.Definition?.Settings as PlexServerSettings)
                    .FirstOrDefault(s => s != null && s.Host.IsNotNullOrWhiteSpace());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read configured Plex Media Server connections");
                return null;
            }
        }

        public void Sync(long listId, string playlistTitle, IReadOnlyList<PlayEventRecord> events)
        {
            // The track cache is intentionally NOT cleared here: it is seeded from the persisted
            // per-list cache (SeedTrackCache) before this call and exported back afterward. Only the
            // per-pass connection caches reset.
            _cachedMachineId = null;
            _cachedMusicSectionIds = null;

            if (events == null || events.Count == 0)
            {
                _logger.Debug("Companion Plex playlist '{0}' has no plays in the window; skipping", playlistTitle);
                return;
            }

            var plex = FindPlexSettings();
            if (plex == null)
            {
                _logger.Debug("Companion Plex playlist '{0}' enabled but no Plex Media Server connection is configured", playlistTitle);
                return;
            }

            var baseUrl = BuildBaseUrl(plex);
            var machineId = GetMachineId(baseUrl, plex);
            if (string.IsNullOrWhiteSpace(machineId))
            {
                _logger.Warn("Could not determine Plex machine identifier; skipping companion playlist '{0}'", playlistTitle);
                return;
            }

            var sectionIds = GetMusicSectionIds(baseUrl, plex);
            if (sectionIds.Count == 0)
            {
                _logger.Warn("No Plex music library found; skipping companion playlist '{0}'", playlistTitle);
                return;
            }

            var ratingKeys = ResolveRatingKeys(baseUrl, plex, sectionIds, events);

            if (ratingKeys.Count == 0)
            {
                _logger.Debug("No companion playlist '{0}' tracks matched the Plex library; leaving playlist untouched", playlistTitle);
                return;
            }

            var playlistRatingKey = EnsurePlaylist(baseUrl, plex, machineId, playlistTitle);
            if (playlistRatingKey.IsNullOrWhiteSpace())
            {
                _logger.Warn("Could not find or create Plex playlist '{0}'", playlistTitle);
                return;
            }

            ReplaceItems(baseUrl, plex, playlistRatingKey!, machineId, ratingKeys);

            _logger.Info("Synced companion Plex playlist '{0}' with {1} tracks", playlistTitle, ratingKeys.Count);
        }

        private List<string> ResolveRatingKeys(string baseUrl, PlexServerSettings plex, List<long> sectionIds, IReadOnlyList<PlayEventRecord> events)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Dedupe by the aired play first (multi-artist plays have one row per artist) so a
            // duet/feature produces one playlist entry, then search once per unique (artist, title).
            foreach (var playGroup in events.GroupBy(e => e.PlayId, StringComparer.OrdinalIgnoreCase))
            {
                var plays = playGroup.ToList();
                var songValue = plays.FirstOrDefault(p => p.Song.IsNotNullOrWhiteSpace())?.Song;
                if (songValue.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var song = songValue!;

                // Try each credited artist (a duet/feature row carries one artist) and take the
                // first that matches the Plex library.
                string? ratingKey = null;
                foreach (var play in plays)
                {
                    if (play.Artist.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    var cacheKey = $"{play.Artist}||{song}";
                    if (!_trackRatingKeyCache.TryGetValue(cacheKey, out var cached))
                    {
                        cached = SearchTrack(baseUrl, plex, sectionIds, play.Artist, song);
                        _trackRatingKeyCache[cacheKey] = cached;
                    }

                    if (cached.IsNotNullOrWhiteSpace())
                    {
                        ratingKey = cached;
                        break;
                    }
                }

                if (ratingKey.IsNotNullOrWhiteSpace() && seen.Add(ratingKey!))
                {
                    result.Add(ratingKey!);
                }
            }

            return result;
        }

        private string? SearchTrack(string baseUrl, PlexServerSettings plex, List<long> sectionIds, string artist, string song)
        {
            // Two-tier matching (curatorr-inspired): try an exact title match first, then a fuzzy
            // match that also tolerates version/remaster/live suffixes.
            foreach (var sectionId in sectionIds)
            {
                var url = $"{baseUrl}/library/sections/{sectionId}/all?type=10&artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(song)}&X-Plex-Container-Size=25";
                var response = Get(url, plex);

                if (response == null)
                {
                    continue;
                }

                var matches = response["MediaContainer"]?["Metadata"] as JArray;
                if (matches == null)
                {
                    continue;
                }

                var exactTitle = NormalizeTitleExact(song);
                var fuzzyTitle = NormalizeTitleFuzzy(song);
                var artistKeys = NormalizeArtistKeys(artist);

                var exactHit = MatchTitle(matches, exactTitle, artistKeys, fuzzy: false);
                if (exactHit != null)
                {
                    return exactHit;
                }

                var fuzzyHit = MatchTitle(matches, fuzzyTitle, artistKeys, fuzzy: true);
                if (fuzzyHit != null)
                {
                    return fuzzyHit;
                }
            }

            return null;
        }

        private static string? MatchTitle(JArray matches, string normalizedTitle, IReadOnlyList<string> artistKeys, bool fuzzy)
        {
            foreach (var match in matches)
            {
                var matchTitle = match["title"]?.Value<string>();
                if (matchTitle.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var candidateTitle = fuzzy ? NormalizeTitleFuzzy(matchTitle!) : NormalizeTitleExact(matchTitle!);
                if (candidateTitle != normalizedTitle)
                {
                    continue;
                }

                var matchArtist = match["grandparentTitle"]?.Value<string>()
                                  ?? match["artist"]?.Value<string>()
                                  ?? match["originalTitle"]?.Value<string>();
                if (matchArtist.IsNotNullOrWhiteSpace() && ArtistMatches(matchArtist!, artistKeys))
                {
                    return match["ratingKey"]?.Value<string>();
                }
            }

            return null;
        }

        // Normalizes each credited artist into a set of comparison keys. Includes the whole
        // feat-stripped name (so "Kool & The Gang" stays a single key) plus one key per
        // comma/ampersand-separated part (so "Run-DMC, Aerosmith" / ["Run-DMC","Aerosmith"] can
        // match on any single credited artist). Whole-name keys are checked before part keys.
        private static IReadOnlyList<string> NormalizeArtistKeys(string artist)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var whole = Normalize(StripFeat(artist));
            if (whole.IsNotNullOrWhiteSpace())
            {
                keys.Add(whole!);
            }

            foreach (var part in SplitArtists(artist))
            {
                var key = Normalize(StripFeat(part));
                if (key.IsNotNullOrWhiteSpace())
                {
                    keys.Add(key!);
                }
            }

            return keys.ToList();
        }

        private static bool ArtistMatches(string matchArtist, IReadOnlyList<string> artistKeys)
        {
            var whole = Normalize(StripFeat(matchArtist));
            if (whole.IsNotNullOrWhiteSpace() && artistKeys.Contains(whole))
            {
                return true;
            }

            foreach (var part in SplitArtists(matchArtist))
            {
                var key = Normalize(StripFeat(part));
                if (key.IsNotNullOrWhiteSpace() && artistKeys.Contains(key))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> SplitArtists(string value)
        {
            return value.Split(new[] { ',', '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.IsNotNullOrWhiteSpace());
        }

        private string? EnsurePlaylist(string baseUrl, PlexServerSettings plex, string machineId, string playlistTitle)
        {
            // Prefer an existing playlist we created (persisted ratingKey). First-run fallback:
            // find by title among audio playlists.
            var existing = FindAudioPlaylistByTitle(baseUrl, plex, playlistTitle);
            if (existing.IsNotNullOrWhiteSpace())
            {
                return existing;
            }

            var createUrl = $"{baseUrl}/playlists?type=audio&title={Uri.EscapeDataString(playlistTitle)}&smart=0&uri=server://{machineId}/com.plexapp.plugins.library";
            var created = Get(createUrl, plex, HttpMethod.Post);
            return created?["MediaContainer"]?["Metadata"]?.FirstOrDefault()?["ratingKey"]?.Value<string>();
        }

        private string? FindAudioPlaylistByTitle(string baseUrl, PlexServerSettings plex, string playlistTitle)
        {
            var normalized = Normalize(playlistTitle);
            var offset = 0;

            while (true)
            {
                var url = $"{baseUrl}/playlists?playlistType=audio&X-Plex-Container-Start={offset}&X-Plex-Container-Size=50";
                var response = Get(url, plex);
                if (response == null)
                {
                    return null;
                }

                var container = response["MediaContainer"];
                var metadata = container?["Metadata"] as JArray;
                if (metadata == null || metadata.Count == 0)
                {
                    return null;
                }

                foreach (var playlist in metadata)
                {
                    var title = playlist["title"]?.Value<string>();
                    if (title.IsNotNullOrWhiteSpace() && Normalize(title!) == normalized)
                    {
                        return playlist["ratingKey"]?.Value<string>();
                    }
                }

                var totalSize = container?["totalSize"]?.Value<int?>() ?? container?["size"]?.Value<int?>() ?? 0;
                offset += metadata.Count;
                if (metadata.Count < 50 || (totalSize > 0 && offset >= totalSize))
                {
                    return null;
                }
            }
        }

        private void ReplaceItems(string baseUrl, PlexServerSettings plex, string playlistRatingKey, string machineId, List<string> ratingKeys)
        {
            var existing = GetPlaylistRatingKeys(baseUrl, plex, playlistRatingKey);

            if (existing.SequenceEqual(ratingKeys, StringComparer.OrdinalIgnoreCase))
            {
                _logger.Debug("Companion Plex playlist contents unchanged; skipping rewrite");
                return;
            }

            Get($"{baseUrl}/playlists/{playlistRatingKey}/items", plex, HttpMethod.Delete);

            for (var i = 0; i < ratingKeys.Count; i += AddBatchSize)
            {
                var batch = ratingKeys.Skip(i).Take(AddBatchSize);
                var uri = $"server://{machineId}/com.plexapp.plugins.library/library/metadata/{string.Join(",", batch)}";
                Get($"{baseUrl}/playlists/{playlistRatingKey}/items?uri={Uri.EscapeDataString(uri)}", plex, HttpMethod.Put);
            }
        }

        private List<string> GetPlaylistRatingKeys(string baseUrl, PlexServerSettings plex, string playlistRatingKey)
        {
            var response = Get($"{baseUrl}/playlists/{playlistRatingKey}/items", plex);
            var metadata = response?["MediaContainer"]?["Metadata"] as JArray;
            if (metadata == null)
            {
                return new List<string>();
            }

            return metadata
                .Select(m => m["ratingKey"]?.Value<string>())
                .Where(k => k.IsNotNullOrWhiteSpace())
                .Cast<string>()
                .ToList();
        }

        private string? GetMachineId(string baseUrl, PlexServerSettings plex)
        {
            if (_cachedMachineId.IsNotNullOrWhiteSpace())
            {
                return _cachedMachineId;
            }

            var response = Get($"{baseUrl}/identity", plex);
            _cachedMachineId = response?["MediaContainer"]?["machineIdentifier"]?.Value<string>();
            return _cachedMachineId;
        }

        private List<long> GetMusicSectionIds(string baseUrl, PlexServerSettings plex)
        {
            if (_cachedMusicSectionIds != null)
            {
                return _cachedMusicSectionIds;
            }

            var result = new List<long>();
            var response = Get($"{baseUrl}/library/sections", plex);
            var sections = response?["MediaContainer"]?["Directory"] as JArray;
            if (sections != null)
            {
                foreach (var section in sections)
                {
                    // Plex music libraries expose type "artist"; audio tracks live under them.
                    if (string.Equals(section["type"]?.Value<string>(), "artist", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = section["key"]?.Value<long?>() ?? section["id"]?.Value<long?>();
                        if (id.HasValue)
                        {
                            result.Add(id.Value);
                        }
                    }
                }
            }

            _cachedMusicSectionIds = result;
            return result;
        }

        private static string BuildBaseUrl(PlexServerSettings plex)
        {
            var scheme = plex.UseSsl ? "https" : "http";
            var host = plex.Host.ToUrlHost();
            var urlBase = (plex.UrlBase ?? "").Trim().TrimEnd('/');
            return $"{scheme}://{host}:{plex.Port}{urlBase}";
        }

        private JToken? Get(string url, PlexServerSettings plex, HttpMethod? method = null)
        {
            for (var attempt = 0; attempt < SearchAttempts; attempt++)
            {
                var response = ExecuteOnce(url, plex, method, out var authTokenAdded);
                if (response != null)
                {
                    return response;
                }

                if (authTokenAdded && attempt < SearchAttempts - 1)
                {
                    _logger.Debug("Plex request failed, retrying ({0}/{1}): {2}", attempt + 1, SearchAttempts, url);
                    Thread.Sleep(RetryDelaysMs[Math.Min(attempt + 1, RetryDelaysMs.Length - 1)]);
                }
            }

            return null;
        }

        private JToken? ExecuteOnce(string url, PlexServerSettings plex, HttpMethod? method, out bool authTokenAdded)
        {
            authTokenAdded = false;
            try
            {
                if (plex.AuthToken.IsNotNullOrWhiteSpace())
                {
                    url += (url.Contains('?') ? "&" : "?") + $"X-Plex-Token={Uri.EscapeDataString(plex.AuthToken!)}";
                    authTokenAdded = true;
                }

                var request = new HttpRequest(url, HttpAccept.Json);
                if (method != null)
                {
                    request.Method = method;
                }

                var response = _httpClient.Execute(request);
                if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content.IsNotNullOrWhiteSpace())
                {
                    return JToken.Parse(response.Content);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.Warn("Plex rejected the configured connection token; skipping companion playlist sync");
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Plex request failed: {0}", url);
                return null;
            }
        }

        // Exact title tier: strip featured-artist credits only, preserving meaningful parens like
        // "(acoustic)" or "(Fade Out)".
        private static string NormalizeTitleExact(string value) => Normalize(StripFeat(value));

        // Fuzzy title tier: also strips trailing parenthetical/bracket version suffixes
        // (Remastered), (Live), [Remix], (from the series ...) so a version mismatch still matches.
        private static string NormalizeTitleFuzzy(string value)
        {
            var result = StripFeat(value);
            result = Regex.Replace(result, @"\s*\([^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s*\([^)]*\)\s*$", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s*\[[^\]]*\]\s*$", "", RegexOptions.IgnoreCase);
            return Normalize(result);
        }

        // Strips featured-artist credits appended to a primary name: "(feat. X)", "(ft. X)",
        // "(featuring X)", or trailing "feat. X" without parens.
        private static string StripFeat(string value)
        {
            var result = Regex.Replace(value, @"\s*\(\s*(?:feat\.?|ft\.?|f\/|featuring)\b[^)]*\)", "", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"\s+(?:feat\.?|ft\.?|f\/|featuring)\b.+$", "", RegexOptions.IgnoreCase);
            return result;
        }

        // Lowercase, collapse whitespace, drop punctuation/symbols so "Kool & The Gang" and
        // "Foo Fighters" match Plex's normalized titles.
        private static string Normalize(string value)
        {
            var builder = new System.Text.StringBuilder();
            var lastWasSpace = false;
            foreach (var c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(c);
                    lastWasSpace = false;
                }
                else if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            return builder.ToString().Trim();
        }
    }
}
