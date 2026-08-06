using System.Collections.Generic;
using NzbDrone.Core.ImportLists;

namespace SXMPlaylist.ImportLists
{
    public class SXMPlaylistRequestGenerator : IImportListRequestGenerator
    {
        public SXMPlaylistImportSettings? Settings { get; set; }

        public int MaxPages { get; set; }
        public int PageSize { get; set; }

        public SXMPlaylistRequestGenerator()
        {
            MaxPages = 1;
            PageSize = 1000;
        }

        public virtual ImportListPageableRequestChain GetListItems()
        {
            var pageableRequests = new ImportListPageableRequestChain();
            pageableRequests.Add(GetRequests());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetRequests()
        {
            var settings = Settings!;
            var baseUrl = settings.BaseUrl.TrimEnd('/');

            yield return new ImportListRequest(SXMPlaylistRequestBuilder.Build($"{baseUrl}/api/station/{settings.Channel}"));
        }
    }
}
