using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistImport : HttpImportListBase<XmPlaylistImportSettings>
    {
        private static readonly TimeSpan ChannelCacheLifetime = TimeSpan.FromHours(24);

        private readonly XmPlaylistHistoryStore _historyStore;
        private readonly XmPlaylistAlbumResolver _albumResolver;

        public override string Name => "XM Playlist";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public override int PageSize => 1000;

        public XmPlaylistImport(
            IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IAppFolderInfo appFolderInfo,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
            _historyStore = new XmPlaylistHistoryStore(appFolderInfo);
            _albumResolver = new XmPlaylistAlbumResolver(httpClient, logger);
        }

        public override IImportListRequestGenerator GetRequestGenerator()
        {
            return new XmPlaylistRequestGenerator
            {
                Settings = Settings
            };
        }

        public override IParseImportListResponse GetParser()
        {
            return new XmPlaylistParser
            {
                Settings = Settings,
                HistoryStore = _historyStore,
                AlbumResolver = _albumResolver
            };
        }

        protected override ImportListResponse FetchImportListResponse(ImportListRequest request)
        {
            _historyStore.PruneOldPlays();
            return XmPlaylistStationBackfill.Fetch(request, MinRefreshInterval, r => XmPlaylistFeedCache.Get(_httpClient, r));
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
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new
                    {
                        Value = c.Deeplink,
                        Name = c.Number.IsNotNullOrWhiteSpace() ? $"{c.Number} - {c.Name}" : c.Name
                    })
            };
        }
    }
}
