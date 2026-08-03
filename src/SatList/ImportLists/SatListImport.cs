using System;
using System.Collections.Generic;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;

namespace SatList.ImportLists
{
    public class SatListImport : HttpImportListBase<SatListImportSettings>
    {
        public override string Name => "SatList Import";

        public override ImportListType ListType => ImportListType.Other;

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public override int PageSize => 100;

        public SatListImport(
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
            return new SatListRequestGenerator
            {
                Settings = Settings
            };
        }

        public override IParseImportListResponse GetParser()
        {
            return new SatListParser();
        }
    }
}
