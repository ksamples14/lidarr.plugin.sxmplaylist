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

namespace XmPlaylist.ImportLists
{
    // Background worker, hosted by the plugin (see Plugin.cs). It owns everything the import lists
    // used to do inline:
    //
    //   - watches Lidarr's import-list definitions for XM Playlist channels (idle if none exist),
    //   - captures each channel's feed (2h cursor backfill) when it's due (~hourly), recording plays
    //     and per-track resolution inputs,
    //   - resolves due tracks' albums (Deezer -> MusicBrainz, Apple fallback) with a 3-strike give-up,
    //     throttled to MusicBrainz's 1 req/s,
    //   - rolls the history forward (prune plays/tracks older than the retention window).
    //
    // The import lists themselves only query the DB for resolved-and-within-window tracks.
    public class XmPlaylistWorker
    {
        private static readonly TimeSpan BackfillWindow = TimeSpan.FromHours(2);
        private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(60);
        private const int ResolutionBatchSize = 50;

        private const string BaseUrl = "https://xmplaylist.com";
        private const string ImplementationName = "XmPlaylistImport";

        private readonly IHttpClient _httpClient;
        private readonly IImportListFactory _importListFactory;
        private readonly XmPlaylistHistoryStore _historyStore;
        private readonly XmPlaylistAlbumResolver _albumResolver;
        private readonly Logger _logger;

        private readonly object _lifecycleLock = new();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _loopTask;

        public XmPlaylistWorker(IHttpClient httpClient, IAppFolderInfo appFolderInfo, IImportListFactory importListFactory, Logger logger)
        {
            _httpClient = httpClient;
            _importListFactory = importListFactory;
            _historyStore = new XmPlaylistHistoryStore(appFolderInfo);
            _albumResolver = new XmPlaylistAlbumResolver(httpClient, logger);
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

                _logger.Info("XM Playlist background worker started");
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
                    _logger.Error(ex, "XM Playlist worker pass failed");
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

            _logger.Info("XM Playlist background worker stopped");
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

            _historyStore.Prune();
        }

        private List<string> GetConfiguredChannels()
        {
            try
            {
                return _importListFactory.All()
                    .Where(d => string.Equals(d.Implementation, ImplementationName, StringComparison.OrdinalIgnoreCase) && d.EnableAutomaticAdd)
                    .Select(d => (d.Settings as XmPlaylistImportSettings)?.Channel)
                    .Where(c => c.IsNotNullOrWhiteSpace())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()!;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read configured XM Playlist channels");
                return new List<string>();
            }
        }

        private bool IsCaptureDue(string channel)
        {
            var lastCapture = _historyStore.GetLastCaptureUtc(channel);
            return lastCapture == null || DateTime.UtcNow - lastCapture.Value >= XmPlaylistHistoryStore.CaptureInterval;
        }

        private void CaptureChannel(string channel, CancellationToken token)
        {
            var request = new ImportListRequest(XmPlaylistRequestBuilder.Build($"{BaseUrl}/api/station/{channel}"));
            var response = XmPlaylistStationBackfill.Fetch(request, BackfillWindow, r => XmPlaylistFeedCache.Get(_httpClient, r));

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
                foreach (var artist in play.Track.Artists)
                {
                    if (artist.IsNullOrWhiteSpace())
                    {
                        continue;
                    }

                    isNew |= _historyStore.TryRecordPlay(play.Id!, playChannel, artist, song, play.Timestamp);
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

        private void ResolveDueTracks(CancellationToken token)
        {
            var due = _historyStore.GetDueTracks(ResolutionBatchSize);
            if (due.Count == 0)
            {
                return;
            }

            foreach (var track in due)
            {
                token.ThrowIfCancellationRequested();

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
                var resolution = _albumResolver.Resolve(artist, track.Song, links);

                if (resolution.Resolved)
                {
                    _historyStore.MarkTrackResolved(track.TrackId, resolution);
                    _logger.Debug("Resolved album for {0} - {1}", artist, track.Song);
                }
                else
                {
                    _historyStore.RecordTrackFailure(track.TrackId);
                }
            }
        }
    }
}
