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
            PageSize = 100;
        }

        public virtual ImportListPageableRequestChain GetListItems()
        {
            var pageableRequests = new ImportListPageableRequestChain();
            pageableRequests.Add(GetRequest());
            return pageableRequests;
        }

        private IEnumerable<ImportListRequest> GetRequest()
        {
            var request = new HttpRequest(Settings.ApiUrl, HttpAccept.Json);

            if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                var paramName = string.IsNullOrWhiteSpace(Settings.ApiKeyParameterName)
                    ? "api_key"
                    : Settings.ApiKeyParameterName;

                if (Settings.ApiKeyLocation == (int)ApiKeyLocation.Header)
                {
                    request.Headers.Add(paramName, Settings.ApiKey);
                }
                else
                {
                    request.AddQueryParam(paramName, Settings.ApiKey);
                }
            }

            yield return new ImportListRequest(request);
        }
    }
}
