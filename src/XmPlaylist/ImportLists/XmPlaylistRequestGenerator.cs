using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
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
            pageableRequests.Add(GetRequests());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetRequests()
        {
            var settings = Settings!;
            var baseUrl = settings.BaseUrl.TrimEnd('/');

            if ((XmPlaylistListMode)settings.ListMode == XmPlaylistListMode.Channel && settings.Channel.IsNotNullOrWhiteSpace())
            {
                yield return BuildRequest($"{baseUrl}/api/station/{settings.Channel}");
                yield break;
            }

            yield return BuildRequest($"{baseUrl}/api/feed");
        }

        private ImportListRequest BuildRequest(string url)
        {
            var request = new HttpRequest(url, HttpAccept.Json);
            request.Headers.Add("User-Agent", "XmPlaylist-Lidarr-Plugin/1.0");
            return new ImportListRequest(request);
        }
    }
}
