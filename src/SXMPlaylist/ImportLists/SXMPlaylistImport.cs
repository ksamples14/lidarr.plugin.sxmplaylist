using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.ThingiProvider;

namespace SXMPlaylist.ImportLists
{
    /// <summary>
    /// Import list that discovers artists from the xmplaylist.com SiriusXM play feed.
    ///
    /// This is a thin shell: all downloading, recording and album resolution happens in the
    /// background <see cref="SXMPlaylistWorker"/>; <see cref="Fetch"/> is just a DB query for this
    /// channel's resolved tracks within the presentation window. Lidarr's import-list processing is
    /// idempotent, so returning the same resolved track across hourly fetches is harmless.
    /// </summary>
    public class SXMPlaylistImport : HttpImportListBase<SXMPlaylistImportSettings>
    {
        private static readonly TimeSpan ChannelCacheLifetime = TimeSpan.FromHours(24);
        private readonly IImportListRepository _importListRepository;
        private readonly IAlbumService _albumService;
        private readonly SXMPlaylistHistoryStore _historyStore;
        private readonly SXMPlaylistRefreshScheduler _refreshScheduler;

        public override string Name => "SXM Playlist";

        public override ProviderMessage Message => new(
            "Album resolution runs in the background and is throttled to MusicBrainz's " +
            "1 request/second limit, so a brand-new channel populates gradually rather than " +
            "all at once. See: https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting",
            ProviderMessageType.Warning
        );

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);

        public override int PageSize => 1000;

        public SXMPlaylistImport(
            IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IAppFolderInfo appFolderInfo,
            IArtistService artistService,
            IAlbumService albumService,
            IManageCommandQueue commandQueueManager,
            IImportListRepository importListRepository,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
            _importListRepository = importListRepository;
            _albumService = albumService;
            _historyStore = new SXMPlaylistHistoryStore(appFolderInfo);
            _refreshScheduler = new SXMPlaylistRefreshScheduler(artistService, albumService, commandQueueManager, logger);
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var channel = Settings?.Channel ?? "";
            if (channel.IsNullOrWhiteSpace())
            {
                return new List<ImportListItemInfo>();
            }

            var now = DateTime.UtcNow;
            var presentationSince = now - SXMPlaylistHistoryStore.PresentationWindow;
            var retainedSince = now - SXMPlaylistHistoryStore.PlayRetention;
            var show = Settings?.Show ?? SXMPlaylistShowSchedule.ChannelValue;
            var windows = GetShowWindows(channel, show);

            // The daily budget is "new albums per day", not "presentations per day": a covered album
            // (already monitored or on disk) must not consume budget. Fetch the whole presentation
            // window (bounded by channel play rate, a few hundred rows) and apply the budget in
            // memory after skipping covered albums and deduplicating by album.
            var albumsPerFetch = GetAlbumsPerFetch(Settings, now);
            var presentable = _historyStore.GetPresentableTracks(
                channel,
                presentationSince,
                retainedSince,
                int.MaxValue,
                windows,
                Settings?.RequireMusicBrainzId ?? false,
                GetMinimumPlays(Settings),
                Settings?.ReleasePriority ?? ReleasePriorityMode.Singles);

            var items = new List<ImportListItemInfo>();
            var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uncoveredAlbums = 0;
            foreach (var track in presentable)
            {
                if (IsAlreadyCovered(track))
                {
                    continue;
                }

                var albumKey = track.AlbumMusicBrainzId.IsNotNullOrWhiteSpace()
                    ? track.AlbumMusicBrainzId!
                    : $"{track.Album}|{string.Join("|", track.Artists)}";
                if (!seenAlbums.Add(albumKey))
                {
                    continue;
                }

                if (albumsPerFetch != int.MaxValue && uncoveredAlbums >= albumsPerFetch)
                {
                    break;
                }

                uncoveredAlbums++;

                foreach (var artist in track.Artists)
                {
                    var item = new ImportListItemInfo
                    {
                        Artist = artist,
                        Album = track.Album ?? "",
                        AlbumMusicBrainzId = track.AlbumMusicBrainzId ?? "",
                        ReleaseDate = track.TimestampUtc
                    };

                    if (track.Artists.Count == 1)
                    {
                        item.ArtistMusicBrainzId = track.ArtistMusicBrainzId ?? "";
                    }

                    items.Add(item);
                }
            }

            // The base-class fetch pipeline normally stamps each item with the list id (and dedupes);
            // our overridden Fetch() bypasses it, so do it here or ImportListSyncService can't match
            // the items back to this list.
            var result = CleanupListItems(items);

            _refreshScheduler.Schedule(result);

            return result;
        }

        private bool IsAlreadyCovered(PresentableTrack track)
        {
            if (IsCoveredAlbum(track.AlbumMusicBrainzId, out var reason))
            {
                _logger.Debug("Skipping '{0}' - '{1}' by {2} because album '{3}' is {4}", track.Song, track.Album, string.Join(", ", track.Artists), track.AlbumMusicBrainzId, reason);
                return true;
            }

            if (IsCoveredAlbum(track.AlternateAlbumMusicBrainzId, out reason))
            {
                _logger.Debug("Skipping '{0}' - '{1}' by {2} because alternate album '{3}' is {4}", track.Song, track.Album, string.Join(", ", track.Artists), track.AlternateAlbumMusicBrainzId, reason);
                return true;
            }

            return false;
        }

        private bool IsCoveredAlbum(string? albumMusicBrainzId, out string reason)
        {
            reason = "";
            if (albumMusicBrainzId.IsNullOrWhiteSpace())
            {
                return false;
            }

            var album = _albumService.FindById(albumMusicBrainzId!);
            if (album == null)
            {
                return false;
            }

            if (album.Monitored)
            {
                reason = "monitored";
                return true;
            }

            try
            {
                var artist = album.Artist?.Value;
                if (artist != null && _albumService.GetArtistAlbumsWithFiles(artist).Any(a => string.Equals(a.ForeignAlbumId, album.ForeignAlbumId, StringComparison.OrdinalIgnoreCase) || (album.Id > 0 && a.Id == album.Id)))
                {
                    reason = "already on disk";
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not determine whether album '{0}' is on disk", albumMusicBrainzId);
                return false;
            }
        }

        public override IImportListRequestGenerator GetRequestGenerator()
        {
            return new SXMPlaylistRequestGenerator
            {
                Settings = Settings
            };
        }

        // The parser is retired from the emit path - the background worker owns feed parsing now.
        public override IParseImportListResponse GetParser()
        {
            return null!;
        }

        // Lidarr's default TestConnection() fetches the feed directly. Keep it a lightweight
        // connectivity check: a single un-backfilled page, no parsing, no album resolution.
        protected override ValidationFailure TestConnection()
        {
            try
            {
                var generator = GetRequestGenerator();
                var request = generator.GetListItems().GetAllTiers().First().First();
                var response = SXMPlaylistFeedCache.Get(_httpClient, request.HttpRequest);

                if (response.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    return new ValidationFailure(string.Empty, $"xmplaylist API returned status {response.StatusCode}");
                }

                var results = response.Content.IsNotNullOrWhiteSpace()
                    ? JObject.Parse(response.Content)["results"] as JArray
                    : null;

                if (results == null || results.Count == 0)
                {
                    return new ValidationFailure(string.Empty, "No results were returned from your import list, please check your settings.");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to xmplaylist");
                return new ValidationFailure(string.Empty, "Unable to connect to xmplaylist: " + ex.Message);
            }

            return null!;
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action == "getShows")
            {
                var channel = Settings?.Channel ?? "";
                if (query.TryGetValue("channel", out var queryChannel) && queryChannel.IsNotNullOrWhiteSpace())
                {
                    channel = queryChannel;
                }

                var shows = new List<ShowInfo>();
                try
                {
                    shows = SXMPlaylistShowSchedule.Fetch(_httpClient, channel, GetChannelName(channel)).ToList();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to refresh SiriusXM EPG show list for channel {0}", channel);
                }

                var currentShow = Settings?.Show ?? SXMPlaylistShowSchedule.ChannelValue;
                var usedShows = GetUsedShowsForChannel(channel);

                return new
                {
                    options = new[]
                        {
                            new { Value = SXMPlaylistShowSchedule.ChannelValue, Name = "Channel" }
                        }
                        .Concat(shows.Select(s => new { Value = s.ProgramId, Name = s.Name }))
                        .Where(s => !usedShows.Contains(NormalizeShow(s.Value)) || string.Equals(NormalizeShow(s.Value), NormalizeShow(currentShow), StringComparison.OrdinalIgnoreCase))
                };
            }

            if (action != "getChannels")
            {
                return base.RequestAction(action, query);
            }

            var cacheAge = _historyStore.GetChannelCacheAge();
            var isStale = cacheAge == null || DateTime.UtcNow - cacheAge.Value > ChannelCacheLifetime;

            if (isStale)
            {
                try
                {
                    var fresh = SXMPlaylistChannelDirectory.Fetch(_httpClient, Settings.BaseUrl);
                    if (fresh.Count > 0)
                    {
                        _historyStore.SaveChannels(fresh);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Failed to refresh xmplaylist channel list, serving cached list if available");
                }
            }

            var channels = _historyStore.GetCachedChannels();
            var currentChannel = Settings?.Channel ?? "";

            return new
            {
                options = channels
                    .Where(c => !GetUsedShowsForChannel(c.Deeplink).Contains(SXMPlaylistShowSchedule.ChannelValue) || string.Equals(c.Deeplink, currentChannel, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(c => int.TryParse(c.Number, out var n) ? n : int.MaxValue)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new
                    {
                        Value = c.Deeplink,
                        Name = c.Number.IsNotNullOrWhiteSpace() ? $"{c.Number} - {c.Name}" : c.Name
                    })
            };
        }

        // A handful of ready-made presets so a channel list can be added in one click.
        public override IEnumerable<ProviderDefinition> DefaultDefinitions
        {
            get
            {
                var presets = new[]
                {
                    GetPreset("Alt Nation", "altnation"),
                    GetPreset("Lithium", "lithium"),
                    GetPreset("PopRocks", "poprocks")
                };

                foreach (var preset in presets)
                {
                    var settings = (SXMPlaylistImportSettings)preset.Settings;
                    if (!GetUsedShowsForChannel(settings.Channel).Contains(SXMPlaylistShowSchedule.ChannelValue))
                    {
                        yield return preset;
                    }
                }
            }
        }

        private static ImportListDefinition GetPreset(string name, string channel) => new()
        {
            EnableAutomaticAdd = true,
            Name = $"{name} ({SXMPlaylistImportSettings.PluginName})",
            Implementation = nameof(SXMPlaylistImport),
            Settings = new SXMPlaylistImportSettings { Channel = channel }
        };

        private IReadOnlyList<ShowWindow>? GetShowWindows(string channel, string show)
        {
            if (show.IsNullOrWhiteSpace())
            {
                return null;
            }

            try
            {
                return SXMPlaylistShowSchedule.Fetch(_httpClient, channel, GetChannelName(channel))
                    .FirstOrDefault(s => string.Equals(s.ProgramId, show, StringComparison.OrdinalIgnoreCase))
                    ?.Windows ?? Array.Empty<ShowWindow>();
            }
            catch (Exception ex)
            {
                // The EPG is unreachable — fall back to the worker's persisted show windows for this
                // program so a show-filtered list keeps presenting from the last known schedule
                // instead of returning nothing (an empty window set short-circuits presentation).
                var cached = _historyStore.GetCachedShowWindows(channel!, show);
                if (cached.Count > 0)
                {
                    _logger.Debug("EPG refresh failed for channel {0}; using {1} cached show windows for '{2}'", channel, cached.Count, show);
                    return cached.Select(w => new ShowWindow(w.StartUtc, w.EndUtc)).ToList();
                }

                _logger.Debug(ex, "Failed to refresh SiriusXM EPG for channel {0} and no cached windows; falling back to empty show window", channel);
                return Array.Empty<ShowWindow>();
            }
        }

        private string? GetChannelName(string channel)
        {
            return _historyStore.GetCachedChannels()
                .FirstOrDefault(c => string.Equals(c.Deeplink, channel, StringComparison.OrdinalIgnoreCase))
                ?.Name;
        }

        private HashSet<string> GetUsedShowsForChannel(string channel)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (channel.IsNullOrWhiteSpace())
            {
                return result;
            }

            try
            {
                foreach (var definition in _importListRepository.All())
                {
                    if (!string.Equals(definition.Implementation, nameof(SXMPlaylistImport), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (Definition != null && definition.Id == Definition.Id)
                    {
                        continue;
                    }

                    if (definition.Settings is not SXMPlaylistImportSettings settings ||
                        !string.Equals(settings.Channel, channel, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    result.Add(NormalizeShow(settings.Show));
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read configured SXM Playlist show filters");
            }

            return result;
        }

        private static string NormalizeShow(string? show)
        {
            return show.IsNullOrWhiteSpace() ? SXMPlaylistShowSchedule.ChannelValue : show!;
        }

        internal static int GetAlbumsPerFetch(SXMPlaylistImportSettings? settings, DateTime nowUtc)
        {
            var albumsPerDay = GetAlbumsPerDay(settings);
            if (albumsPerDay == 0)
            {
                return int.MaxValue;
            }

            var baseHourly = albumsPerDay / 24;
            var remainder = albumsPerDay % 24;

            return baseHourly + (nowUtc.Hour < remainder ? 1 : 0);
        }

        private static int GetAlbumsPerDay(SXMPlaylistImportSettings? settings)
        {
            var value = settings?.AlbumsPerDay ?? 24;
            return value < 0 ? 24 : Math.Clamp(value, 0, 500);
        }

        private static int GetMinimumPlays(SXMPlaylistImportSettings? settings)
        {
            return Math.Max(settings?.MinimumPlays ?? 1, 1);
        }
    }
}
