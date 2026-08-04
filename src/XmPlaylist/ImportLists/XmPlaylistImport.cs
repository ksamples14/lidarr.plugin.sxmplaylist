using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.Parser;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistImport : HttpImportListBase<XmPlaylistImportSettings>
    {
        private readonly XmPlaylistStateStore _stateStore;

        public override string Name => "XM Playlist";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public override int PageSize => 1000;

        public XmPlaylistImport(
            IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            IDiskProvider diskProvider,
            IAppFolderInfo appFolderInfo,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
            _stateStore = new XmPlaylistStateStore(diskProvider, appFolderInfo);
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
                StateStore = _stateStore,
                ListId = Definition?.Id ?? 0
            };
        }

        protected override ImportListResponse FetchImportListResponse(ImportListRequest request)
        {
            var response = XmPlaylistFeedCache.Get(_httpClient, request.HttpRequest);
            return new ImportListResponse(request, response);
        }
    }
}
