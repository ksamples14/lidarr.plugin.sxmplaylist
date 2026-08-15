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
using System.Xml.Linq;

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
        private static readonly TimeSpan PlexTvUserCacheInterval = TimeSpan.FromHours(24);

        private const string PlexTvBaseUrl = "https://plex.tv";
        private const string PlexClientIdentifier = "lidarr.sxmplaylist";
        private const string PlexProduct = "Lidarr.Plugin.SXMPlaylist";
        private const string PlexPlatform = "Web";

        private readonly IHttpClient _httpClient;
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        // Per-pass caches (reset per list sync or Sync call).
        private readonly Dictionary<string, string?> _trackRatingKeyCache = new(StringComparer.OrdinalIgnoreCase);
        private string? _cachedMachineId;
        private List<long>? _cachedMusicSectionIds;
        private readonly CachedPlexTvValue<string?> _ownerUserIdCache = new();
        private readonly CachedPlexTvValue<List<PlexSharedUser>> _sharedUsersCache = new();
        private readonly CachedPlexTvValue<List<PlexHomeUser>> _homeUsersCache = new();

        public SXMPlaylistPlexClient(IHttpClient httpClient, INotificationFactory notificationFactory, Logger logger)
        {
            _httpClient = httpClient;
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        // Clears the track cache. Called before each list sync to prevent cross-list contamination.
        public void ClearTrackCache()
        {
            _trackRatingKeyCache.Clear();
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

        public PlexSyncResult Sync(long listId, string playlistTitle, IReadOnlyList<PlayEventRecord> events)
        {
            var result = new PlexSyncResult();

            // Reset per-pass connection caches. The track cache is managed externally by the caller:
            // ClearTrackCache() before the list sync, SeedTrackCache() from the persisted per-list
            // cache, then ExportTrackCache() to persist after.
            _cachedMachineId = null;
            _cachedMusicSectionIds = null;

            if (events == null || events.Count == 0)
            {
                _logger.Debug("Companion Plex playlist '{0}' has no plays in the window; skipping", playlistTitle);
                return result;
            }

            var plex = FindPlexSettings();
            if (plex == null)
            {
                _logger.Debug("Companion Plex playlist '{0}' enabled but no Plex Media Server connection is configured", playlistTitle);
                return result;
            }

            var baseUrl = BuildBaseUrl(plex);
            var machineId = GetMachineId(baseUrl, plex);
            if (string.IsNullOrWhiteSpace(machineId))
            {
                _logger.Warn("Could not determine Plex machine identifier; skipping companion playlist '{0}'", playlistTitle);
                return result;
            }

            var sectionIds = GetMusicSectionIds(baseUrl, plex);
            if (sectionIds.Count == 0)
            {
                _logger.Warn("No Plex music library found; skipping companion playlist '{0}'", playlistTitle);
                return result;
            }

            var ratingKeys = ResolveRatingKeys(baseUrl, plex, sectionIds, events);

            if (ratingKeys.Count == 0)
            {
                _logger.Debug("No companion playlist '{0}' tracks matched the Plex library; leaving playlist untouched", playlistTitle);
                return result;
            }

            var playlistRatingKey = EnsurePlaylist(baseUrl, plex, machineId, playlistTitle);
            if (playlistRatingKey.IsNullOrWhiteSpace())
            {
                _logger.Warn("Could not find or create Plex playlist '{0}'", playlistTitle);
                return result;
            }

            ReplaceItems(baseUrl, plex, playlistRatingKey!, machineId, ratingKeys);

            result.OwnerPlaylistRatingKey = playlistRatingKey!;
            _logger.Info("Synced companion Plex playlist '{0}' with {1} tracks", playlistTitle, ratingKeys.Count);

            // Fan out a copy of the playlist to every user with library access (shared users like
            // vsa191) plus any Plex Home members without a share, each in their own Playlists tab.
            // Owner-created playlists only auto-appear for managed users, so everyone else needs a
            // playlist created in their own account.
            FanOutToSharedUsers(plex, playlistTitle, machineId, ratingKeys, result);

            return result;
        }

        // Creates/updates the same playlist in every user's account (except the owner,
        // whose playlist was already synced). The target set is every user with library access
        // (shared_servers), since those are the accounts that can actually see and use the playlist;
        // Plex Home members who aren't in shared_servers (e.g. managed sub-accounts without a share)
        // are still included via the home-user switch API. Best-effort: unreachable users
        // (PIN-protected, no library access, plex.tv failures) are skipped with a warning and never
        // fail the owner sync.
        private void FanOutToSharedUsers(PlexServerSettings plex, string playlistTitle, string machineId, List<string> ratingKeys, PlexSyncResult result)
        {
            if (plex.AuthToken.IsNullOrWhiteSpace())
            {
                return;
            }

            string? ownerUserId;
            try
            {
                ownerUserId = GetOwnerUserId(plex.AuthToken!);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not resolve the Plex owner id; falling back to the admin flag");
                ownerUserId = null;
            }

            // shared_servers (XML) is the primary target list AND token source: every user with
            // library access plus their direct server access token in one call.
            List<PlexSharedUser> sharedUsers;
            try
            {
                sharedUsers = GetSharedServerUsers(plex.AuthToken!, machineId);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read Plex shared users; skipping companion playlist fan-out for '{0}'", playlistTitle);
                return;
            }

            // Home members supplement the shared list (managed sub-accounts don't appear in
            // shared_servers). They need the switch + resources exchange when they have no share.
            List<PlexHomeUser> homeUsers;
            try
            {
                homeUsers = GetHomeUsers(plex.AuthToken!);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not enumerate Plex Home users; continuing with shared users only");
                homeUsers = new List<PlexHomeUser>();
            }

            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sharedByUserId = sharedUsers.ToDictionary(u => u.UserId, StringComparer.OrdinalIgnoreCase);

            foreach (var user in sharedUsers)
            {
                if (ShouldSkipUser(user.UserId, user.Username, ownerUserId) != null)
                {
                    continue;
                }

                // Only users with the music library shared can see (and use) the companion playlist;
                // everyone else would get an empty, pointless playlist in their account.
                if (!user.HasMusicShare)
                {
                    _logger.Debug("Skipping Plex user '{0}' for companion playlist '{1}': no music library share", user.Username, playlistTitle);
                    continue;
                }

                handled.Add(user.UserId);
                SyncPlaylistForUser(plex, playlistTitle, machineId, ratingKeys, result, user.Username, user.UserId, user.AccessToken, user.IsProtected);
            }

            foreach (var homeUser in homeUsers)
            {
                if (ShouldSkipUser(homeUser.Id, homeUser.Title, ownerUserId) != null || handled.Contains(homeUser.Id))
                {
                    continue;
                }

                var shared = sharedByUserId.TryGetValue(homeUser.Id, out var sharedUser) ? sharedUser : null;
                if (shared != null && !shared.HasMusicShare)
                {
                    _logger.Debug("Skipping Plex home user '{0}' for companion playlist '{1}': no music library share", homeUser.Title, playlistTitle);
                    continue;
                }

                var token = shared?.AccessToken;
                if (token.IsNullOrWhiteSpace())
                {
                    try
                    {
                        token = GetUserServerToken(homeUser, plex.AuthToken!, machineId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Could not obtain a server token for Plex home user '{0}'", homeUser.Title);
                        token = null;
                    }
                }

                handled.Add(homeUser.Id);
                SyncPlaylistForUser(plex, playlistTitle, machineId, ratingKeys, result, homeUser.Title, homeUser.Id, token, homeUser.IsProtected);
            }
        }

        private void SyncPlaylistForUser(PlexServerSettings plex, string playlistTitle, string machineId, List<string> ratingKeys, PlexSyncResult result, string displayName, string userId, string? userToken, bool isProtected)
        {
            if (userToken.IsNullOrWhiteSpace())
            {
                var reason = isProtected
                    ? "PIN-protected home user cannot be switched"
                    : "no server access token could be obtained";
                result.SkippedUsers.Add($"{displayName}: {reason}");
                _logger.Warn("Could not obtain a Plex server token for user '{0}'; skipping companion playlist '{1}'", displayName, playlistTitle);
                return;
            }

            var userPlex = CloneWithToken(plex, userToken!);
            var userBaseUrl = BuildBaseUrl(userPlex);
            try
            {
                var userKey = EnsurePlaylist(userBaseUrl, userPlex, machineId, playlistTitle);
                if (userKey.IsNullOrWhiteSpace())
                {
                    result.SkippedUsers.Add($"{displayName}: could not find or create the playlist");
                    _logger.Warn("Could not find or create playlist '{0}' for user '{1}'", playlistTitle, displayName);
                    return;
                }

                ReplaceItems(userBaseUrl, userPlex, userKey!, machineId, ratingKeys);
                result.UserPlaylistRatingKeys[userId] = userKey!;
                _logger.Info("Synced companion Plex playlist '{0}' to user '{1}' with {2} tracks", playlistTitle, displayName, ratingKeys.Count);
            }
            catch (Exception ex)
            {
                result.SkippedUsers.Add($"{displayName}: playlist sync failed");
                _logger.Warn(ex, "Companion Plex playlist '{0}' sync failed for user '{1}'", playlistTitle, displayName);
            }
        }

        // The owner account is identified by its plex.tv user id (resolved from the token's identity).
        // IsAdmin is used only as a fallback when the id lookup fails, matching Plex Home's single-admin
        // model so a full-account member isn't incorrectly skipped.
        private static string? ShouldSkipUser(string userId, string displayName, string? ownerUserId)
        {
            if (ownerUserId.IsNotNullOrWhiteSpace() && userId == ownerUserId)
            {
                return "owner account";
            }

            return null;
        }

        // Deletes a companion playlist and every fan-out copy previously created for it. Called by
        // the worker when the "Companion Plex Playlist" option is switched off (or the list is
        // deleted) so the playlists don't linger as orphans.
        //
        // Returns true when cleanup completed (the owner copy was deleted and, if there were user
        // copies, every one was attempted). Returns false when plex.tv was unreachable so the caller
        // keeps the persisted state and retries next cycle instead of leaving orphaned copies with
        // no record to recover them from.
        public bool CleanupPlaylist(string playlistTitle, string ownerPlaylistRatingKey, IReadOnlyDictionary<string, string> userPlaylistKeys)
        {
            var plex = FindPlexSettings();
            if (plex == null)
            {
                _logger.Debug("Companion Plex playlist '{0}' cleanup skipped: no Plex Media Server connection is configured", playlistTitle);
                return false;
            }

            var baseUrl = BuildBaseUrl(plex);

            if (ownerPlaylistRatingKey.IsNotNullOrWhiteSpace())
            {
                DeletePlaylist(baseUrl, plex, ownerPlaylistRatingKey);
            }

            if (userPlaylistKeys.Count == 0 || plex.AuthToken.IsNullOrWhiteSpace())
            {
                return true;
            }

            // Resolve each fan-out user's server token so the playlist can be deleted from their
            // account. shared_servers provides direct tokens. If plex.tv is unreachable we cannot
            // resolve tokens, so report incomplete and let the caller retry later.
            var tokenByUserId = GetSharedUserTokens(plex, baseUrl);
            if (tokenByUserId == null)
            {
                _logger.Warn("Playlist '{0}' cleanup incomplete: plex.tv unreachable, will retry next cycle", playlistTitle);
                return false;
            }

            foreach (var pair in userPlaylistKeys)
            {
                var userId = pair.Key;
                var ratingKey = pair.Value;
                if (ratingKey.IsNullOrWhiteSpace())
                {
                    continue;
                }

                if (tokenByUserId.TryGetValue(userId, out var shared))
                {
                    var userPlex = CloneWithToken(plex, shared.AccessToken);
                    DeletePlaylist(BuildBaseUrl(userPlex), userPlex, ratingKey);
                }
                else
                {
                    _logger.Debug("Playlist '{0}' cleanup skipped for user id '{1}': no server token available", playlistTitle, userId);
                }
            }

            return true;
        }

        // Removes the playlist copies for users who no longer have the music library shared (e.g.
        // their share was edited in Plex while the Lidarr option stays on). Returns the keys that
        // should be retained in the persisted per-user map. Best-effort and idempotent.
        //
        // - User still has music shared -> key retained.
        // - User in shared_servers but music no longer shared -> copy deleted, key dropped.
        // - plex.tv unreachable -> nothing verifiable, ALL keys retained so nothing is dropped
        //   incorrectly.
        // - User absent from shared_servers entirely (fully unshared / removed) -> key dropped: we
        //   cannot manage that account anymore, and keeping a stale key risks collisions if the user
        //   is later re-added.
        public IReadOnlyDictionary<string, string> PruneUnsharedPlaylistCopies(string playlistTitle, IReadOnlyDictionary<string, string> userPlaylistKeys)
        {
            var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (userPlaylistKeys.Count == 0)
            {
                return retained;
            }

            var plex = FindPlexSettings();
            if (plex == null || plex.AuthToken.IsNullOrWhiteSpace())
            {
                _logger.Debug("Companion Plex playlist '{0}' unshare-prune skipped: no Plex connection configured", playlistTitle);
                foreach (var pair in userPlaylistKeys)
                {
                    retained[pair.Key] = pair.Value;
                }

                return retained;
            }

            var baseUrl = BuildBaseUrl(plex);
            var tokenByUserId = GetSharedUserTokens(plex, baseUrl);
            if (tokenByUserId == null)
            {
                // plex.tv unreachable: cannot verify current shares, keep everything.
                _logger.Debug("Companion Plex playlist '{0}' unshare-prune deferred: plex.tv unreachable", playlistTitle);
                foreach (var pair in userPlaylistKeys)
                {
                    retained[pair.Key] = pair.Value;
                }

                return retained;
            }

            foreach (var pair in userPlaylistKeys)
            {
                var userId = pair.Key;
                var ratingKey = pair.Value;
                if (ratingKey.IsNullOrWhiteSpace())
                {
                    continue;
                }

                // User still present with music shared -> keep the copy.
                if (tokenByUserId.TryGetValue(userId, out var shared))
                {
                    if (shared.HasMusicShare)
                    {
                        retained[userId] = ratingKey;
                        continue;
                    }

                    if (shared.AccessToken.IsNotNullOrWhiteSpace())
                    {
                        var userPlex = CloneWithToken(plex, shared.AccessToken);
                        DeletePlaylist(BuildBaseUrl(userPlex), userPlex, ratingKey);
                        _logger.Info("Removed companion Plex playlist '{0}' from user id '{1}': music library no longer shared", playlistTitle, userId);
                    }

                    continue;
                }

                // User fully removed from shared_servers: drop the stale key.
                _logger.Debug("Playlist '{0}' key dropped for user id '{1}': user no longer in shared_servers", playlistTitle, userId);
            }

            return retained;
        }

        // Resolves fan-out user server tokens from shared_servers (direct tokens). Returns null when
        // plex.tv was unreachable (callers should not draw conclusions from an empty result), and a
        // (possibly empty) map when the lookup succeeded. Duplicate user ids are collapsed.
        private Dictionary<string, PlexSharedUser>? GetSharedUserTokens(PlexServerSettings plex, string baseUrl)
        {
            try
            {
                var machineId = GetMachineId(baseUrl, plex);
                if (machineId.IsNullOrWhiteSpace())
                {
                    return null;
                }

                // Distinguish "plex.tv unreachable" (no payload) from "valid response, no users":
                // the former must not be treated as an authoritative empty set.
                if (!TryGetSharedServerUsers(plex.AuthToken!, machineId!, out var users))
                {
                    return null;
                }

                return users
                    .GroupBy(u => u.UserId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not resolve shared user tokens for playlist cleanup");
                return null;
            }
        }

        private static PlexServerSettings CloneWithToken(PlexServerSettings source, string token)
        {
            return new PlexServerSettings
            {
                Host = source.Host,
                Port = source.Port,
                UseSsl = source.UseSsl,
                UrlBase = source.UrlBase,
                AuthToken = token
            };
        }

        private sealed class PlexHomeUser
        {
            public string Id { get; set; } = "";
            public string Uuid { get; set; } = "";
            public string Title { get; set; } = "";
            public string Username { get; set; } = "";
            public string Email { get; set; } = "";
            public bool IsRestricted { get; set; }
            public bool IsProtected { get; set; }
            public bool IsAdmin { get; set; }
        }

        private sealed class CachedPlexTvValue<T>
        {
            public string OwnerToken { get; set; } = "";
            public string MachineId { get; set; } = "";
            public DateTime CachedUtc { get; set; } = DateTime.MinValue;
            public bool HasValue { get; set; }
            public T? Value { get; set; }
        }

        private sealed class PlexTvDevice
        {
            public string ClientIdentifier { get; set; } = "";
            public string AccessToken { get; set; } = "";
        }

        // GET https://plex.tv/api/v2/home/users with the owner token lists every member of the
        // Plex Home: the admin/owner, managed (restricted) sub-accounts, and full accounts with
        // their own plex.tv identity. Restricted=1 identifies managed users; protected=1 means a
        // PIN is set (which the switch API needs and we cannot supply for non-managed users).
        private static List<PlexHomeUser> ParseHomeUsers(string content)
        {
            var result = new List<PlexHomeUser>();
            if (content.IsNullOrWhiteSpace())
            {
                return result;
            }

            var json = JToken.Parse(content);
            var users = ToJArray(json?["MediaContainer"]?["User"] ?? json?["users"]);
            if (users == null)
            {
                return result;
            }

            foreach (var u in users)
            {
                result.Add(new PlexHomeUser
                {
                    Id = u["id"]?.Value<string>() ?? "",
                    Uuid = u["uuid"]?.Value<string>() ?? "",
                    Title = u["title"]?.Value<string>() ?? u["username"]?.Value<string>() ?? "",
                    Username = u["username"]?.Value<string>() ?? "",
                    Email = u["email"]?.Value<string>() ?? "",
                    IsRestricted = IsFlag(u, "restricted"),
                    IsProtected = IsFlag(u, "protected"),
                    IsAdmin = IsFlag(u, "admin")
                });
            }

            return result;
        }

        // Some Plex endpoints serialize a singleton collection as a bare object instead of an array.
        private static JArray? ToJArray(JToken? token)
        {
            switch (token)
            {
                case null:
                    return null;
                case JArray array:
                    return array;
                case JObject obj:
                    return new JArray(obj);
                default:
                    return null;
            }
        }

        private static bool IsFlag(JToken token, string property)
        {
            var value = token[property]?.Value<string>();
            if (value.IsNullOrWhiteSpace())
            {
                return false;
            }

            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static List<PlexTvDevice> ParseResources(string content)
        {
            var result = new List<PlexTvDevice>();
            if (content.IsNullOrWhiteSpace())
            {
                return result;
            }

            var json = JToken.Parse(content);
            var devices = ToJArray(json?["MediaContainer"]?["Device"]);
            if (devices == null)
            {
                return result;
            }

            foreach (var d in devices)
            {
                result.Add(new PlexTvDevice
                {
                    ClientIdentifier = d["clientIdentifier"]?.Value<string>() ?? d["clientidentifier"]?.Value<string>() ?? "",
                    AccessToken = d["accessToken"]?.Value<string>() ?? ""
                });
            }

            return result;
        }

        private string? GetHomeUsersJson(string ownerToken)
        {
            var request = new HttpRequest($"{PlexTvBaseUrl}/api/v2/home/users", HttpAccept.Json);
            AddPlexTvHeaders(request, ownerToken);
            return ExecutePlexTv(request);
        }

        private string? GetOwnerUserIdJson(string ownerToken)
        {
            var request = new HttpRequest($"{PlexTvBaseUrl}/api/v2/user", HttpAccept.Json);
            AddPlexTvHeaders(request, ownerToken);
            return ExecutePlexTv(request);
        }

        private string? GetSharedServersJson(string machineId, string ownerToken)
        {
            var request = new HttpRequest($"{PlexTvBaseUrl}/api/servers/{Uri.EscapeDataString(machineId)}/shared_servers", HttpAccept.Json);
            AddPlexTvHeaders(request, ownerToken);
            return ExecutePlexTv(request);
        }

        private string? SwitchHomeUser(string uuid, string ownerToken)
        {
            var request = new HttpRequest($"{PlexTvBaseUrl}/api/v2/home/users/{Uri.EscapeDataString(uuid)}/switch", HttpAccept.Json)
            {
                Method = HttpMethod.Post
            };
            request.Headers.ContentType = "application/x-www-form-urlencoded";
            AddPlexTvHeaders(request, ownerToken);
            return ExecutePlexTv(request);
        }

        private string? GetResourcesJson(string switchedToken)
        {
            var request = new HttpRequest($"{PlexTvBaseUrl}/api/v2/resources?includeHttps=1&includeRelay=1", HttpAccept.Json);
            AddPlexTvHeaders(request, switchedToken);
            return ExecutePlexTv(request);
        }

        private static void AddPlexTvHeaders(HttpRequest request, string token)
        {
            request.Headers.Add("X-Plex-Token", token);
            request.Headers.Add("X-Plex-Client-Identifier", PlexClientIdentifier);
            request.Headers.Add("X-Plex-Product", PlexProduct);
            request.Headers.Add("X-Plex-Platform", PlexPlatform);
        }

        private string? ExecutePlexTv(HttpRequest request)
        {
            for (var attempt = 0; attempt < SearchAttempts; attempt++)
            {
                try
                {
                    var response = _httpClient.Execute(request);
                    if (response.StatusCode == System.Net.HttpStatusCode.OK && response.Content.IsNotNullOrWhiteSpace())
                    {
                        return response.Content;
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        _logger.Warn("Plex.tv rejected the token; cannot sync companion playlists to Plex Home users");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Plex.tv request failed: {0}", request.Url);
                }

                if (attempt < SearchAttempts - 1)
                {
                    Thread.Sleep(RetryDelaysMs[Math.Min(attempt + 1, RetryDelaysMs.Length - 1)]);
                }
            }

            return null;
        }

        private List<PlexHomeUser> GetHomeUsers(string ownerToken)
        {
            if (IsPlexTvCacheFresh(_homeUsersCache, ownerToken, null))
            {
                return _homeUsersCache.Value!.ToList();
            }

            var content = GetHomeUsersJson(ownerToken);
            if (content.IsNullOrWhiteSpace())
            {
                throw new InvalidOperationException("Plex.tv returned no home users payload");
            }

            var users = ParseHomeUsers(content!);
            CachePlexTvValue(_homeUsersCache, ownerToken, null, users);
            return users;
        }

        private string? GetOwnerUserId(string ownerToken)
        {
            if (IsPlexTvCacheFresh(_ownerUserIdCache, ownerToken, null))
            {
                return _ownerUserIdCache.Value;
            }

            var content = GetOwnerUserIdJson(ownerToken);
            if (content.IsNullOrWhiteSpace())
            {
                return null;
            }

            var json = JToken.Parse(content!);
            var ownerUserId = json?["id"]?.Value<string>();
            CachePlexTvValue(_ownerUserIdCache, ownerToken, null, ownerUserId);
            return ownerUserId;
        }

        // Resolves a home member's server access token via the home-user switch + resources exchange.
        // Used only for home members who don't appear in shared_servers (managed sub-accounts).
        // PIN-protected users can't be switched without their PIN, so they resolve to null and are
        // skipped by the caller.
        private string? GetUserServerToken(PlexHomeUser user, string ownerToken, string machineId)
        {
            if (user.IsProtected)
            {
                _logger.Debug("Plex home user '{0}' is PIN-protected; cannot switch to obtain a server token", user.Title);
                return null;
            }

            var switchedContent = SwitchHomeUser(user.Uuid, ownerToken);
            if (switchedContent.IsNullOrWhiteSpace())
            {
                return null;
            }

            var authToken = ExtractAuthToken(switchedContent!);
            if (authToken.IsNullOrWhiteSpace())
            {
                return null;
            }

            var resourcesContent = GetResourcesJson(authToken!);
            if (resourcesContent.IsNullOrWhiteSpace())
            {
                return null;
            }

            var devices = ParseResources(resourcesContent!);
            var match = devices.FirstOrDefault(d => string.Equals(d.ClientIdentifier, machineId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                _logger.Debug("Plex resources for home user '{0}' had no device matching machineId '{1}'", user.Title, machineId);
            }

            return match?.AccessToken;
        }

        // Fetches shared_servers once per sync pass and returns every user with library access plus
        // their direct server access token. This legacy endpoint returns XML regardless of the Accept
        // header: <MediaContainer><SharedServer id=".." username=".." userID=".." accessToken="..">.
        private List<PlexSharedUser> GetSharedServerUsers(string ownerToken, string machineId)
        {
            TryGetSharedServerUsers(ownerToken, machineId, out var users);
            return users;
        }

        private bool TryGetSharedServerUsers(string ownerToken, string machineId, out List<PlexSharedUser> users)
        {
            if (IsPlexTvCacheFresh(_sharedUsersCache, ownerToken, machineId))
            {
                users = _sharedUsersCache.Value!.ToList();
                return true;
            }

            var content = GetSharedServersJson(machineId, ownerToken);
            if (content.IsNullOrWhiteSpace())
            {
                users = new List<PlexSharedUser>();
                return false;
            }

            users = ParseSharedServerUsers(content!);
            CachePlexTvValue(_sharedUsersCache, ownerToken, machineId, users);
            return true;
        }

        private static bool IsPlexTvCacheFresh<T>(CachedPlexTvValue<T> cache, string ownerToken, string? machineId)
        {
            return cache.HasValue
                   && string.Equals(cache.OwnerToken, ownerToken, StringComparison.Ordinal)
                   && string.Equals(cache.MachineId, machineId ?? "", StringComparison.OrdinalIgnoreCase)
                   && DateTime.UtcNow - cache.CachedUtc < PlexTvUserCacheInterval;
        }

        private static void CachePlexTvValue<T>(CachedPlexTvValue<T> cache, string ownerToken, string? machineId, T value)
        {
            cache.OwnerToken = ownerToken;
            cache.MachineId = machineId ?? "";
            cache.CachedUtc = DateTime.UtcNow;
            cache.Value = value;
            cache.HasValue = true;
        }

        private static List<PlexSharedUser> ParseSharedServerUsers(string content)
        {
            var result = new List<PlexSharedUser>();
            var document = XDocument.Parse(content);
            var root = document.Root;
            if (root == null)
            {
                return result;
            }

            foreach (var server in root.Elements("SharedServer"))
            {
                var userId = server.Attribute("userID")?.Value ?? "";
                var username = server.Attribute("username")?.Value ?? server.Attribute("title")?.Value ?? "";
                var token = server.Attribute("accessToken")?.Value ?? "";
                if (userId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                result.Add(new PlexSharedUser
                {
                    UserId = userId,
                    Username = username,
                    AccessToken = token,
                    IsProtected = server.Attribute("protected")?.Value == "1",
                    HasMusicShare = HasMusicSection(server)
                });
            }

            return result;
        }

        // The shared_servers payload lists every library section shared with a user, each with a
        // shared="1"/"0" flag. The music library is the artist-typed section titled "Music" (the
        // same type the plugin resolves tracks against). A user only gets a companion playlist when
        // the music library is actually shared with them; creating playlists for users without
        // music access produces empty, useless playlists.
        private static bool HasMusicSection(XElement server)
        {
            foreach (var section in server.Elements("Section"))
            {
                var type = section.Attribute("type")?.Value ?? "";
                var title = section.Attribute("title")?.Value ?? "";
                var shared = section.Attribute("shared")?.Value;
                if (!string.Equals(type, "artist", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(title, "Music", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (shared == "1")
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class PlexSharedUser
        {
            public string UserId { get; set; } = "";
            public string Username { get; set; } = "";
            public string AccessToken { get; set; } = "";
            public bool IsProtected { get; set; }

            // True when the user's share includes the music (artist) library.
            public bool HasMusicShare { get; set; }
        }

        private static string? ExtractAuthToken(string content)
        {
            if (content.IsNullOrWhiteSpace())
            {
                return null;
            }

            var json = JToken.Parse(content);
            return json?["authToken"]?.Value<string>()
                   ?? json?["user"]?["authToken"]?.Value<string>();
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
            var titleSearchTerms = GetTitleSearchTerms(song);

            foreach (var sectionId in sectionIds)
            {
                var exactTitle = NormalizeTitleExact(song);
                var fuzzyTitle = NormalizeTitleFuzzy(song);
                var artistKeys = NormalizeArtistKeys(artist);

                foreach (var titleSearchTerm in titleSearchTerms)
                {
                    var url = $"{baseUrl}/library/sections/{sectionId}/all?type=10&artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(titleSearchTerm)}&X-Plex-Container-Size=25";
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

        // Deletes a playlist from a user's account. Best-effort: a failure is logged and swallowed
        // so cleanup never aborts the worker pass. A null response (e.g. HTTP 404 for an already
        // deleted playlist) is treated as success-for-idempotency but logged at Debug, not Warn.
        private void DeletePlaylist(string baseUrl, PlexServerSettings plex, string playlistRatingKey)
        {
            try
            {
                var response = Get($"{baseUrl}/playlists/{playlistRatingKey}", plex, HttpMethod.Delete);
                if (response != null)
                {
                    _logger.Info("Deleted Plex playlist ratingKey {0}", playlistRatingKey);
                }
                else
                {
                    _logger.Debug("Plex playlist ratingKey {0} delete returned no payload (already gone or rejected)", playlistRatingKey);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Failed to delete Plex playlist ratingKey {0}", playlistRatingKey);
            }
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

        private static IReadOnlyList<string> GetTitleSearchTerms(string value)
        {
            var terms = new List<string>();
            AddTitleSearchTerm(terms, value);

            var stripped = SXMPlaylistTitleNormalizer.StripTrailingParentheticalSuffixes(StripFeat(value));
            AddTitleSearchTerm(terms, stripped);

            return terms;
        }

        private static void AddTitleSearchTerm(List<string> terms, string value)
        {
            var term = value.Trim();
            if (term.IsNotNullOrWhiteSpace() && !terms.Contains(term, StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term);
            }
        }

        // Fuzzy title tier: also strips trailing parenthetical/bracket version suffixes
        // (Remastered), (Live), [Remix], (from the series ...) so a version mismatch still matches.
        private static string NormalizeTitleFuzzy(string value)
        {
            return Normalize(SXMPlaylistTitleNormalizer.StripTrailingParentheticalSuffixes(StripFeat(value)));
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

    // Result of a companion playlist sync: the owner's playlist rating key (created/updated with the
    // Lidarr-configured token) plus per-home-user playlist keys for every other Plex Home member the
    // plugin was able to reach. Users that couldn't be reached (PIN-protected, no library access, or
    // plex.tv failures) are listed in SkippedUsers so the caller can surface them without failing.
    public class PlexSyncResult
    {
        public string OwnerPlaylistRatingKey { get; set; } = "";

        // Plex Home user id -> playlist ratingKey created in that user's account. Keyed by the stable
        // user id (not the display name, which can collide or change).
        public Dictionary<string, string> UserPlaylistRatingKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Plex Home users that were skipped, each as "username: reason".
        public List<string> SkippedUsers { get; } = new();
    }
}
