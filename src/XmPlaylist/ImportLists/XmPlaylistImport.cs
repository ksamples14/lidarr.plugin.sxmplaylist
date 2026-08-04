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

        // Lidarr's default TestConnection() calls FetchPage(), which goes through our overridden
        // FetchImportListResponse (the full 6-hour cursor backfill) and the full parser (album
        // resolution against Deezer/MusicBrainz/Apple for every newly-seen song). For a first-ever
        // Test against a busy channel that's a lot of work behind one click. Test only needs to
        // confirm the endpoint is reachable and returning data - a single un-backfilled page, with
        // no parsing, does that far faster.
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
