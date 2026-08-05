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

namespace XmPlaylist.ImportLists
{
    // Thin import-list shell. All downloading, recording and album resolution happens in the
    // background XmPlaylistWorker; Fetch() is just a DB query for this channel's resolved tracks
    // within the presentation window. Lidarr's import-list processing is idempotent, so returning
    // the same resolved track across hourly fetches is harmless.
    public class XmPlaylistImport : HttpImportListBase<XmPlaylistImportSettings>
    {
        private static readonly TimeSpan ChannelCacheLifetime = TimeSpan.FromHours(24);
        private const int MaxItemsPerFetch = 20;

        private readonly XmPlaylistHistoryStore _historyStore;
        private readonly XmPlaylistRefreshScheduler _refreshScheduler;

        public override string Name => "XM Playlist";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(1);

        public override int PageSize => 1000;

        public XmPlaylistImport(
            IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IAppFolderInfo appFolderInfo,
            IArtistService artistService,
            IAlbumService albumService,
            IManageCommandQueue commandQueueManager,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
            _historyStore = new XmPlaylistHistoryStore(appFolderInfo);
            _refreshScheduler = new XmPlaylistRefreshScheduler(artistService, albumService, commandQueueManager, logger);
        }

        public override IList<ImportListItemInfo> Fetch()
        {
            var channel = Settings?.Channel ?? "";
            if (channel.IsNullOrWhiteSpace())
            {
                return new List<ImportListItemInfo>();
            }

            var since = DateTime.UtcNow - XmPlaylistHistoryStore.PresentationWindow;
            var presentable = _historyStore.GetPresentableTracks(channel, since, MaxItemsPerFetch);

            var items = new List<ImportListItemInfo>();
            foreach (var track in presentable)
            {
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

            _refreshScheduler.Schedule(items);

            return items;
        }

        public override IImportListRequestGenerator GetRequestGenerator()
        {
            return new XmPlaylistRequestGenerator
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
                var response = XmPlaylistFeedCache.Get(_httpClient, request.HttpRequest);

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
                    var fresh = XmPlaylistChannelDirectory.Fetch(_httpClient, Settings.BaseUrl);
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

            return new
            {
                options = channels
                    .OrderBy(c => int.TryParse(c.Number, out var n) ? n : int.MaxValue)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new
                    {
                        Value = c.Deeplink,
                        Name = c.Number.IsNotNullOrWhiteSpace() ? $"{c.Number} - {c.Name}" : c.Name
                    })
            };
        }
    }
}
