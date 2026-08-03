using System;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistImport : HttpImportListBase<XmPlaylistImportSettings>
    {
        public override string Name => "XM Playlist";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromMinutes(30);

        public override int PageSize => 1000;

        public XmPlaylistImport(
            IHttpClient httpClient,
            IImportListStatusService importListStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, importListStatusService, configService, parsingService, logger)
        {
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
                Settings = Settings
            };
        }
    }
}
