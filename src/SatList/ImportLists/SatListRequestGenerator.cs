using System.Collections.Generic;
using NzbDrone.Common.Http;

namespace SatList.ImportLists
{
    public class SatListRequestGenerator : IImportListRequestGenerator
    {
        public SatListImportSettings Settings { get; set; }

        public int MaxPages { get; set; }
        public int PageSize { get; set; }

        public SatListRequestGenerator()
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
            var request = new HttpRequest($"{Settings.BaseUrl}/api/feed?limit={Settings.ResultCount}", HttpAccept.Json);

            request.Headers.Add("User-Agent", "SatList-Lidarr-Plugin/1.0");

            yield return new ImportListRequest(request);
        }
    }
}
