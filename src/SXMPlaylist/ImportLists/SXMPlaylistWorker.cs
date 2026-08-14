using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Newtonsoft.Json;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Profiles.Metadata;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// <summary>
    /// Background worker, hosted by the plugin (see <c>SXMPlaylistPlugin</c>). It owns everything
    /// the import lists used to do inline:
    /// <list type="bullet">
    /// <item>watches Lidarr's import-list definitions for SXM Playlist channels (idle if none exist),</item>
    /// <item>captures each channel's feed (2h cursor backfill) when it's due (~hourly), recording plays and per-track resolution inputs,</item>
    /// <item>resolves due tracks' albums (Deezer → MusicBrainz, Apple fallback) with a 3-strike give-up, throttled to MusicBrainz's 1 req/s,</item>
    /// <item>rolls the history forward (prune plays/tracks older than the retention window).</item>
    /// </list>
    /// The import lists themselves only query the DB for resolved-and-within-window tracks.
    /// </summary>
    /// </summary>
    public class SXMPlaylistWorker
    {
        private static readonly TimeSpan BackfillWindow = TimeSpan.FromHours(2);
        private static readonly TimeSpan ShowScheduleRefreshInterval = TimeSpan.FromHours(24);
        private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PlexSyncInterval = TimeSpan.FromMinutes(15);
        private const int ResolutionBatchSize = 50;
        private const int RetryBatchSize = 15;

        private const string BaseUrl = "https://xmplaylist.com";
        private const string ImplementationName = "SXMPlaylistImport";

        private readonly IHttpClient _httpClient;
        private readonly IImportListFactory _importListFactory;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly SXMPlaylistHistoryStore _historyStore;
        private readonly SXMPlaylistAlbumResolver _albumResolver;
        private readonly SXMPlaylistPlexClient _plexClient;
        private readonly Logger _logger;

        private readonly object _lifecycleLock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _loopTask;

        public SXMPlaylistWorker(
            IHttpClient httpClient,
            IAppFolderInfo appFolderInfo,
            IImportListFactory importListFactory,
            IMetadataProfileService metadataProfileService,
            INotificationFactory notificationFactory,
            Logger logger)
        {
            _httpClient = httpClient;
            _importListFactory = importListFactory;
            _metadataProfileService = metadataProfileService;
            _historyStore = new SXMPlaylistHistoryStore(appFolderInfo);
            _albumResolver = new SXMPlaylistAlbumResolver(httpClient, logger);
            _plexClient = new SXMPlaylistPlexClient(httpClient, notificationFactory, logger);
            _logger = logger;
        }

        public bool IsRunning
        {
            get
            {
                lock (_lifecycleLock)
                {
                    return _loopTask is { IsCompleted: false };
                }
            }
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_loopTask is { IsCompleted: false })
                {
                    return;
                }

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(_cancellationTokenSource.Token));

                _logger.Info("SXM Playlist background worker started");
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                _cancellationTokenSource?.Cancel();
            }
        }

        private async Task RunLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    RunOnce(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "SXM Playlist worker pass failed");
                }

                try
                {
                    await Task.Delay(LoopInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.Info("SXM Playlist background worker stopped");
        }

        // Separated from the loop so it's unit-testable without threads.
        public void RunOnce(CancellationToken token)
        {
            var channels = GetConfiguredChannels();

            foreach (var channel in channels)
            {
                token.ThrowIfCancellationRequested();

                if (IsCaptureDue(channel))
                {
                    try
                    {
                        CaptureChannel(channel, token);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Failed to capture channel {0}", channel);
                    }
                }
            }

            ResolveDueTracks(token);

            SyncCompanionPlexPlaylists(token);

            _historyStore.Prune();
        }

        private List<string> GetConfiguredChannels()
        {
            try
            {
                return _importListFactory.All()
                    .Where(d => string.Equals(d.Implementation, ImplementationName, StringComparison.OrdinalIgnoreCase) && d.EnableAutomaticAdd)
                    .Select(d => (d.Settings as SXMPlaylistImportSettings)?.Channel)
                    .Where(c => c.IsNotNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read configured SXM Playlist channels");
                return new List<string>();
            }
        }

        private bool IsCaptureDue(string channel)
        {
            var lastCapture = _historyStore.GetLastCaptureUtc(channel);
            return lastCapture == null || DateTime.UtcNow - lastCapture.Value >= SXMPlaylistHistoryStore.CaptureInterval;
        }

        // Refreshes companion Plex playlists for any import list that opted in, throttled to
        // PlexSyncInterval per list. Best-effort: a missing Plex connection or auth failure is
        // logged and ignored, never fatal to the worker pass.
        private void SyncCompanionPlexPlaylists(CancellationToken token)
        {
            List<ImportListDefinition> plexLists;
            try
            {
                plexLists = _importListFactory.All()
                    .Where(d => string.Equals(d.Implementation, ImplementationName, StringComparison.OrdinalIgnoreCase) && d.EnableAutomaticAdd)
                    .Where(d => (d.Settings as SXMPlaylistImportSettings)?.AddCompanionPlexPlaylist == true)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read configured SXM Playlist lists for companion Plex sync");
                return;
            }

            foreach (var definition in plexLists)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    var settings = definition.Settings as SXMPlaylistImportSettings;
                    if (settings == null)
                    {
                        continue;
                    }

                    var state = _historyStore.GetPlexPlaylistState(definition.Id);
                    if (state != null && DateTime.UtcNow - state.LastSyncUtc < PlexSyncInterval)
                    {
                        continue;
                    }

                    var playlistTitle = BuildPlexPlaylistTitle(definition);
                    var lookback = TimeSpan.FromDays(Math.Clamp(settings.HistoryRetentionDays, 1, (int)SXMPlaylistHistoryStore.PlayRetention.TotalDays));
                    var sinceUtc = DateTime.UtcNow - lookback;
                    var programId = settings.Show.IsNullOrWhiteSpace() ? null : settings.Show;

                    var events = _historyStore.GetPlayEvents(settings.Channel, sinceUtc, DateTime.UtcNow, programId);

                    _plexClient.ClearTrackCache();

                    if (state?.TrackCache != null && state.TrackCache.Count > 0)
                    {
                        _plexClient.SeedTrackCache(state.TrackCache);
                    }

                    var syncResult = _plexClient.Sync(definition.Id, playlistTitle, events);

                    // Only persist state when the owner playlist was actually found or created: a
                    // failed pass leaves the stored rating key intact so the next cycle retries instead
                    // of being throttled out by a fresh LastSyncUtc.
                    if (syncResult.OwnerPlaylistRatingKey.IsNotNullOrWhiteSpace())
                    {
                        _historyStore.UpsertPlexPlaylistState(definition.Id, playlistTitle, syncResult.OwnerPlaylistRatingKey, DateTime.UtcNow, _plexClient.ExportTrackCache(), syncResult.UserPlaylistRatingKeys);
                    }

                    if (syncResult.SkippedUsers.Count > 0)
                    {
                        _logger.Warn("Companion Plex playlist '{0}' skipped {1} Plex Home user(s): {2}", playlistTitle, syncResult.SkippedUsers.Count, string.Join(", ", syncResult.SkippedUsers));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Companion Plex playlist sync failed for list '{0}'", definition.Name);
                }
            }
        }

        private static string BuildPlexPlaylistTitle(ImportListDefinition definition)
        {
            var settings = definition.Settings as SXMPlaylistImportSettings;
            var baseTitle = definition.Name.IsNotNullOrWhiteSpace() ? definition.Name : "SXM Playlist";
            var show = settings?.Show ?? SXMPlaylistShowSchedule.ChannelValue;
            return show.IsNullOrWhiteSpace() ? baseTitle : $"{baseTitle} ({show})";
        }

        private void CaptureChannel(string channel, CancellationToken token)
        {
            var request = new ImportListRequest(SXMPlaylistRequestBuilder.Build($"{BaseUrl}/api/station/{channel}"));
            var response = SXMPlaylistStationBackfill.Fetch(request, BackfillWindow, r => SXMPlaylistFeedCache.Get(_httpClient, r));

            if (response.HttpResponse.StatusCode != System.Net.HttpStatusCode.OK || response.Content.IsNullOrWhiteSpace())
            {
                _logger.Warn("Channel {0} returned status {1}, skipping capture", channel, response.HttpResponse.StatusCode);
                return;
            }

            var feed = JsonConvert.DeserializeObject<XmFeedResponse>(response.Content);
            if (feed?.Results == null)
            {
                return;
            }

            RefreshShowWindows(channel);

            var captured = 0;

            foreach (var play in feed.Results)
            {
                token.ThrowIfCancellationRequested();

                if (play.Id.IsNullOrWhiteSpace() || play.Track?.Artists == null || play.Track.Artists.Count == 0)
                {
                    continue;
                }

                var playChannel = play.ChannelId.IsNotNullOrWhiteSpace() ? play.ChannelId! : channel;
                var song = play.Track.Title ?? "";
                var deezerUrl = play.Links?.FirstOrDefault(l => string.Equals(l.Site, "deezer", StringComparison.OrdinalIgnoreCase))?.Url;
                var appleMusicUrl = play.Links?.FirstOrDefault(l => string.Equals(l.Site, "appleMusic", StringComparison.OrdinalIgnoreCase))?.Url;
                var trackId = play.Track.Id;

                if (trackId.IsNullOrWhiteSpace())
                {
                    continue;
                }

                var isNew = false;
                var showWindow = _historyStore.GetShowWindowForPlay(playChannel, play.Timestamp);
                foreach (var artist in play.Track.Artists)
                {
                    if (artist.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    isNew |= _historyStore.TryRecordPlay(play.Id!, playChannel, artist, song, play.Timestamp);
                    isNew |= _historyStore.TryRecordPlayEvent(play.Id!, playChannel, trackId, artist, song, play.Timestamp, showWindow);
                }

                if (isNew)
                {
                    _historyStore.UpsertTrack(trackId!, playChannel, play.Track.Artists, song, deezerUrl, appleMusicUrl, play.Timestamp);
                    captured++;
                }
            }

            _historyStore.SetLastCaptureUtc(channel, DateTime.UtcNow);
            _logger.Debug("Captured {0} new plays for channel {1}", captured, channel);
        }

        private void RefreshShowWindows(string channel)
        {
            try
            {
                var cacheAge = _historyStore.GetShowWindowsCacheAge(channel);
                if (cacheAge != null && DateTime.UtcNow - cacheAge.Value < ShowScheduleRefreshInterval)
                {
                    return;
                }

                var shows = SXMPlaylistShowSchedule.Fetch(_httpClient, channel, GetChannelName(channel));
                if (shows.Count > 0)
                {
                    _historyStore.SaveShowWindows(channel, shows);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to refresh SiriusXM EPG windows for channel {0}", channel);
            }
        }

        private string? GetChannelName(string channel)
        {
            return _historyStore.GetCachedChannels()
                .FirstOrDefault(c => string.Equals(c.Deeplink, channel, StringComparison.OrdinalIgnoreCase))
                ?.Name;
        }

        private void ResolveDueTracks(CancellationToken token)
        {
            var filtersByChannel = BuildFiltersByChannel();

            // Phase 1: first-time resolution gets the full budget and runs first, so the retry
            // backlog can never starve fresh tracks.
            var due = _historyStore.GetDueTracks(ResolutionBatchSize);
            foreach (var track in due)
            {
                token.ThrowIfCancellationRequested();
                ResolveTrack(track, filtersByChannel, isRetry: false, token);
            }

            // Phase 2: no-MBID tracks due for a re-attempt, on a much smaller budget.
            var dueRetries = _historyStore.GetDueRetries(RetryBatchSize, DateTime.UtcNow);
            foreach (var track in dueRetries)
            {
                token.ThrowIfCancellationRequested();
                ResolveTrack(track, filtersByChannel, isRetry: true, token);
            }
        }

        private void ResolveTrack(PendingTrack track, Dictionary<string, AlbumTypeFilter> filtersByChannel, bool isRetry, CancellationToken token)
        {
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (track.DeezerUrl.IsNotNullOrWhiteSpace())
            {
                links["deezer"] = track.DeezerUrl!;
            }

            if (track.AppleMusicUrl.IsNotNullOrWhiteSpace())
            {
                links["appleMusic"] = track.AppleMusicUrl!;
            }

            var artist = track.Artists.FirstOrDefault() ?? "";
            var baseFilter = filtersByChannel.TryGetValue(track.Channel, out var f) ? f : AlbumTypeFilter.Unrestricted;
            var storedAny = false;
            var retryIncomplete = false;
            var resolutions = _albumResolver.ResolveAllPriorities(artist, track.Song, links, baseFilter, track.Artists);

            foreach (var releasePriority in new[] { ReleasePriorityMode.Singles, ReleasePriorityMode.Albums })
            {
                if (!resolutions.TryGetValue(releasePriority, out var resolution) || !resolution.Resolved)
                {
                    retryIncomplete = retryIncomplete || isRetry;
                    continue;
                }

                if (isRetry && resolution.AlbumMusicBrainzId.IsNullOrWhiteSpace())
                {
                    retryIncomplete = true;
                    continue;
                }

                _historyStore.MarkTrackResolved(track.TrackId, releasePriority, resolution);
                storedAny = true;
                _logger.Debug("Resolved {0} album for {1} - {2}", releasePriority, artist, track.Song);
            }

            if (isRetry && retryIncomplete)
            {
                _historyStore.RecordRetryFailure(track.TrackId, DateTime.UtcNow);
                _logger.Debug("Retry {0} - {1} still has unresolved priority slots, will retry later", artist, track.Song);
            }
            else if (!storedAny)
            {
                _historyStore.RecordTrackFailure(track.TrackId);
            }
        }

        // Map each configured channel to the album-type filter derived from that channel's list(s)
        // metadata profile. Multiple lists on the same channel share a profile here; when two lists
        // disagree, the first enabled list wins (same accepted rule as PLAN §6.2). Falls back to
        // unrestricted when no list/profile is available.
        private Dictionary<string, AlbumTypeFilter> BuildFiltersByChannel()
        {
            var result = new Dictionary<string, AlbumTypeFilter>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var definition in _importListFactory.All()
                    .Where(d => string.Equals(d.Implementation, ImplementationName, StringComparison.OrdinalIgnoreCase) && d.EnableAutomaticAdd))
                {
                    var settings = definition.Settings as SXMPlaylistImportSettings;
                    var channel = settings?.Channel;
                    if (channel.IsNullOrWhiteSpace() || result.ContainsKey(channel!))
                    {
                        continue;
                    }

                    result[channel!] = GetFilterForProfileId(definition.MetadataProfileId, settings?.ReleasePriority ?? ReleasePriorityMode.Singles);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read metadata profiles for SXM Playlist channels");
            }

            return result;
        }

        private AlbumTypeFilter GetFilterForProfileId(int metadataProfileId, ReleasePriorityMode releasePriority)
        {
            try
            {
                var profile = _metadataProfileService.Exists(metadataProfileId)
                    ? _metadataProfileService.Get(metadataProfileId)
                    : _metadataProfileService.All().FirstOrDefault();

                if (profile == null)
                {
                    return AlbumTypeFilter.Unrestricted;
                }

                return new AlbumTypeFilter(
                    new HashSet<string>(profile.PrimaryAlbumTypes.Where(p => p.Allowed).Select(p => p.PrimaryAlbumType.Name), StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(profile.SecondaryAlbumTypes.Where(p => p.Allowed).Select(p => p.SecondaryAlbumType.Name), StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(profile.ReleaseStatuses.Where(p => p.Allowed).Select(p => p.ReleaseStatus.Name), StringComparer.OrdinalIgnoreCase),
                    releasePriority);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read metadata profile {0}, using unrestricted filter", metadataProfileId);
                return AlbumTypeFilter.Unrestricted;
            }
        }
    }
}
