using System;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.ImportLists;

namespace XmPlaylist.ImportLists
{
    // xmplaylist's station endpoint only returns ~24 plays per page (a few minutes of history),
    // far short of the 6-hour poll interval. Walk its `next` cursor backwards until the page's
    // oldest play crosses the poll window, then hand the parser one merged result set.
    public static class XmPlaylistStationBackfill
    {
        public const int MaxPages = 50;

        public static ImportListResponse Fetch(ImportListRequest request, TimeSpan window, Func<HttpRequest, HttpResponse> fetchPage)
        {
            var cutoff = DateTime.UtcNow - window;
            var mergedResults = new JArray();
            var currentRequest = request.HttpRequest;
            HttpResponse? lastResponse = null;

            for (var page = 0; page < MaxPages; page++)
            {
                lastResponse = fetchPage(currentRequest);

                if (lastResponse.StatusCode != HttpStatusCode.OK || lastResponse.Content.IsNullOrWhiteSpace())
                {
                    break;
                }

                var json = JObject.Parse(lastResponse.Content);
                var results = json["results"] as JArray ?? new JArray();

                foreach (var item in results)
                {
                    mergedResults.Add(item);
                }

                var oldestTimestamp = results.Count > 0 ? results[results.Count - 1]["timestamp"]?.Value<DateTime>() : null;
                var nextUrl = json["next"]?.Value<string>();

                if (nextUrl.IsNullOrWhiteSpace() || (oldestTimestamp.HasValue && oldestTimestamp.Value <= cutoff))
                {
                    break;
                }

                currentRequest = XmPlaylistRequestBuilder.Build(nextUrl!);
            }

            var mergedContent = new JObject
            {
                ["count"] = mergedResults.Count,
                ["results"] = mergedResults
            }.ToString(Formatting.None);

            var finalResponse = new HttpResponse(
                request.HttpRequest,
                lastResponse?.Headers ?? new HttpHeader(),
                mergedContent,
                lastResponse?.StatusCode ?? HttpStatusCode.OK);

            return new ImportListResponse(request, finalResponse);
        }
    }
}
