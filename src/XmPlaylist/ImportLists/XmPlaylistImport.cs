using System;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistImport : HttpImportListBase<XmPlaylistImportSettings>
    {
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
    }
}
