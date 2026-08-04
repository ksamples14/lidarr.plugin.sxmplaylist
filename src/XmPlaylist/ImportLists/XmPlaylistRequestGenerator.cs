using System.Collections.Generic;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace XmPlaylist.ImportLists
{
    public class XmPlaylistRequestGenerator : IImportListRequestGenerator
    {
        public XmPlaylistImportSettings? Settings { get; set; }

        public int MaxPages { get; set; }
        public int PageSize { get; set; }

        public XmPlaylistRequestGenerator()
        {
            MaxPages = 1;
            PageSize = 1000;
        }

        public virtual ImportListPageableRequestChain GetListItems()
        {
            var pageableRequests = new ImportListPageableRequestChain();
            pageableRequests.Add(GetRequest());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetRequest()
        {
            var settings = Settings!;
            var request = new HttpRequest($"{settings.BaseUrl}/api/feed?limit={settings.ResultCount}", HttpAccept.Json);

            request.Headers.Add("User-Agent", "XmPlaylist-Lidarr-Plugin/1.0");

            yield return new ImportListRequest(request);
        }
    }
}
